using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Musicthingy.Effects;
using Musicthingy.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Musicthingy.Views;

/// <summary>
/// Self-contained lyrics display for the mobile player pane.
/// Owns the three mutually-exclusive lyric states:
/// 1. Timed/synced lyrics (auto-centering on the active line)
/// 2. Static plain-text lyrics (ScrollView)
/// 3. "No lyrics" prompt with a Find Lyrics action
/// The BindingContext is inherited from the host page (MainViewModel),
/// so ActiveTimedLyrics / HasTimedLyrics / HasStaticLyrics / HasNoLyrics /
/// FindLyricsCommand bindings resolve exactly as before.
///
/// Behavior notes:
/// - Centering targets the middle of the VISIBLE viewport (the middle of the
///   view, not of the screen). Half-viewport spacers above the first / below
///   the last line let EVERY line - including the first and last - be
///   centered; the bottom spacer additionally carries the hidden bottom strip
///   (Android nav-bar area) so the VISIBLE blank below the last line matches
///   the one above the first. The empty 0s anchor line collapses to zero
///   height (XAML DataTrigger) so it never adds phantom blank at the top.
///   The expanded pane zone is sized to fit the screen; ComputeHiddenBottom()
///   subtracts the Android navigation-bar area (edge-to-edge) - see
///   LyricsPlatformInsets.
/// - Depth effect is anchored on the HIGHLIGHTED line and is the SAME for
///   auto-follow on and off: an illumination falloff (quadratic, inverse-
///   square-like) - the highlighted line lights up the surrounding rows and
///   reaches the minimum contrast at the SIXTH line (~5.5 visible rows per
///   side, so the 6th is right at the view edge). The highlighted line keeps
///   FULL white/opacity in both modes (the min-contrast rule). The ONLY mode
///   difference is the blur: while AUTO-FOLLOWING the rows around the active
///   one are progressively blurred (barely / readable / more / nearly
///   unreadable / smeared from the edge outward); while BROWSING no blur.
///   Styling is applied PER ROW within a window around the active line, not
///   to every element of the list, and collection-change restyles are
///   coalesced into one pass - both keep the native work bounded (a
///   RenderEffect per far label caused multi-second lag spikes on open).
/// - The fade is the text color/opacity itself; there is no gradient overlay
///   and no viewport-position-based melting, so nothing is ever drawn over
///   the player controls in Medium state.
/// - Auto-follow pauses on any user scroll and resumes on the next lyric
///   switch after 1.5s idle when the active line is near the middle, or after
///   4s idle from ANY position - following can never stay stuck away from
///   the action.
/// - Tapping a timed line raises LyricTapped (host seeks via the ViewModel).
/// </summary>
public partial class LyricsView : ContentView
{
    // Fallback when a label has not been laid out yet (FontSize 22 * LineHeight 1.4 + 6px top/bottom margins)
    private const double EstimatedLineHeight = 43;

    // Spacer heights are quantized so the lyric list is NOT re-measured on
    // every frame of the player's resize animation. Quantization is FINE (8dp)
    // - a coarse 40dp quantum added up to ~20dp of dead padding per spacer.
    private const double SpacerQuantum = 8;

    // Auto-follow resume rules (per spec)
    private const double AutoFollowResumeDelaySeconds = 1.5;
    private const double AutoFollowResumeForcedSeconds = 4.0; // idle after which following resumes from ANY position
    private const double MiddleZoneRatio = 0.25;   // "general area of the middle" = middle 50% of the viewport
    private const double FeedbackToleranceDp = 8;  // measured centering error below this is left alone

    private bool _isFollowing = true;
    private DateTime _lastUserScrollAt = DateTime.MinValue;

    // Programmatic scrolls are guarded by a TIME WINDOW, not a counter. On
    // Android an animated ScrollToAsync that gets superseded/interrupted can
    // leave its Task incomplete forever, which would leak a counter and
    // permanently swallow every Scrolled event - auto-follow could then NEVER
    // be exited by manual scrolling. A time window always expires on its own.
    private DateTime _programmaticScrollUntil = DateTime.MinValue;
    private DateTime _suppressUserScrollUntil = DateTime.MinValue;
    private INotifyCollectionChanged? _hookedCollection;

    // Measured centering residual (dp), learned from the active line's REAL
    // on-screen position (native view coordinates) and pre-applied to every
    // centering target. Absorbs any constant geometry error (hidden-strip
    // estimate, nav inset, native text layout) without modeling it.
    private double _centerBiasDp;

    // Height at which _centerBiasDp was (re)learned. The residual differs per
    // geometry (Medium vs Full screen), so the bias is dropped on size changes
    // - keeping it made the whole list sit visibly too low/high ("offset").
    private double _lastBiasHeight;

    // Last color/opacity applied to each label - styling re-runs whenever the
    // active line or the follow state changes, so redundant native property
    // sets are skipped (labels are recreated on every track change, so the
    // cache is cleared there).
    private readonly Dictionary<Label, (Color Color, double Opacity)> _lastAppliedStyles = new();

    // Coalesces styling after collection changes. The ViewModel rebuilds the
    // lyric list with Clear + N Add events; restyling on EVERY event re-styled
    // the whole growing list N+1 times (O(N^2) native property sets, plus a
    // native RenderEffect per far label in each pass) - the multi-second lag
    // spike when opening lyrics. One queued pass sees the final list instead.
    private bool _restyleScheduled;

    private void ScheduleDistanceStyles(bool clearCache)
    {
        if (clearCache)
            _lastAppliedStyles.Clear(); // labels are recreated - drop stale entries

        if (_restyleScheduled)
            return; // a pass is already queued - it will see the final state

        _restyleScheduled = true;
        Dispatcher.Dispatch(() =>
        {
            _restyleScheduled = false;
            ApplyDistanceStyles();
        });
    }

    // Track-change auto-follow: when the lyric list is rebuilt we ALWAYS resume
    // following and scroll to the start of the new list (the empty 0s anchor
    // line), even if the user was browsing the previous track.
    private bool _pendingStartScroll;
    private int _startScrollAttempts;
    private bool _startScrollTimerRunning;

    /// <summary>Raised when the user taps a timed lyric line. Host should seek to lyric.Time.</summary>
    public event EventHandler<TimedLyric>? LyricTapped;

    public LyricsView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        TimedLyricsScrollView.Scrolled += OnTimedLyricsScrolled;
        LyricsItemsHost.PropertyChanged += OnHostPropertyChanged;
    }

    // ============================== Public API ==============================

    /// <summary>
    /// Centers the given lyric line in the middle of the VISIBLE area.
    /// The offset is computed from the label's REAL laid-out position, so lines
    /// far down the list land exactly on center instead of drifting downward.
    /// </summary>
    /// <param name="allowResume">True when called because the active line switched;
    /// allows paused auto-follow to resume if the other resume conditions hold.</param>
    public void ScrollToLyric(TimedLyric? lyric, bool animate = true, bool allowResume = false)
    {
        if (lyric == null || !lyric.IsActive)
            return;
        if (TimedLyricsScrollView.Height <= 0 || TimedLyricsScrollView.ContentSize.Height <= 0)
            return;

        int index = 0;
        Label? targetLabel = null;
        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is Label lbl && ReferenceEquals(lbl.BindingContext, lyric))
            {
                targetLabel = lbl;
                index = i;
                break;
            }
        }

        if (targetLabel == null)
            return;

        // Restyle IMMEDIATELY on every lyric switch - BEFORE the auto-follow
        // gate below. The gate may legitimately decline to SCROLL (the user is
        // browsing), but the HIGHLIGHT itself must still move: previously the
        // early returns skipped the restyle, so while browsing the white
        // highlight froze on a stale line whenever the new active line was off
        // screen (and the browsing fade never updated either).
        ApplyDistanceStyles(index);

        // Auto-follow gate: while the user is browsing, resume only on a lyric
        // switch, after the user has been idle long enough (see the two-tier
        // rule below - middle zone, or forced from any position after 4s).
        if (!_isFollowing)
        {
            if (!allowResume)
                return;

            // Two-tier resume so following can never stay stuck away from the
            // action: after 1.5s idle it resumes smoothly when the active line
            // is already around the middle; after 4s idle it resumes from ANY
            // position (the user walked far away - snap back instead of
            // remaining desynced until the next track).
            double idleSeconds = (DateTime.UtcNow - _lastUserScrollAt).TotalSeconds;
            if (idleSeconds < AutoFollowResumeDelaySeconds)
                return;
            bool inMiddle = IsInMiddleZone(targetLabel, index);
            if (idleSeconds < AutoFollowResumeForcedSeconds && !inMiddle)
                return;

            _isFollowing = true;
            System.Diagnostics.Debug.WriteLine(
                $"[LyricsView] auto-follow ON (resume, idle {idleSeconds:F1}s{(inMiddle ? "" : ", forced")})");

            // The regime just flipped from browsing to following - re-apply so
            // the full depth look (stronger fade + blur) takes over at once.
            ApplyDistanceStyles(index);
        }

        double hidden = ComputeHiddenBottom();
        double visibleHeight = TimedLyricsScrollView.Height - hidden;
        if (visibleHeight <= 0)
            return; // lyric strip not visible (e.g. Medium state) - styling only

        double unclampedOffset = GetContentCenterY(targetLabel, index) - visibleHeight / 2;
        double maxOffset = Math.Max(0, TimedLyricsScrollView.ContentSize.Height - TimedLyricsScrollView.Height);
        double targetOffset = Math.Clamp(unclampedOffset, 0, maxOffset);
        bool clamped = Math.Abs(unclampedOffset - targetOffset) > 0.5; // line cannot reach center (song start/end)

        // Centering diagnostics - the "feedback" line that follows reports the
        // REAL measured position and any residual bias.
        System.Diagnostics.Debug.WriteLine(
            $"[LyricsView] center: idx={index} lineCenterY={GetContentCenterY(targetLabel, index):F1} " +
            $"viewportH={TimedLyricsScrollView.Height:F1} contentH={TimedLyricsScrollView.ContentSize.Height:F1} " +
            $"hiddenBottom={hidden:F1} visibleH={visibleHeight:F1} target={targetOffset + _centerBiasDp:F1} " +
            $"(raw {unclampedOffset:F1}, max {maxOffset:F1}, bias {_centerBiasDp:F1})");

        // Scroll, then MEASURE the line's real on-screen position, snap it to
        // the true center and learn the residual bias for next time.
        _ = CenterWithFeedbackAsync(targetLabel, targetOffset, animate, allowLearn: !clamped);
    }

    /// <summary>
    /// Re-centers the timed lyrics list on whichever line is currently active.
    /// Used to re-sync the view after a resize animation completes.
    /// </summary>
    public void ScrollToActiveLyric(bool animate = true, bool allowResume = false)
    {
        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is Label lbl && lbl.BindingContext is TimedLyric lyric && lyric.IsActive)
            {
                ScrollToLyric(lyric, animate, allowResume);
                return;
            }
        }
    }

    /// <summary>
    /// Makes all zones visible (without fade) right before a resize animation starts,
    /// so lyric content never pops in mid-animation.
    /// </summary>
    public void PrepareForAnimation()
    {
        NoLyricsPrompt.IsVisible = true;
    }

    /// <summary>
    /// Restores final IsVisible states once a resize animation has fully completed.
    /// </summary>
    public void FinalizeVisibility()
    {
        NoLyricsPrompt.IsVisible = NoLyricsPrompt.Opacity > 0.01;
    }

    /// <summary>
    /// Fades the "no lyrics" prompt in as the player approaches full screen and
    /// keeps the half-viewport paddings in sync while the geometry changes.
    /// </summary>
    public void UpdateForFullScreenProgress(double progressToFull)
    {
        NoLyricsPrompt.Opacity = Math.Clamp(progressToFull * 2, 0, 1);
        EnsureItemsHooked();
        UpdateSpacers();
    }

    // ========================= Centering & geometry =========================

    /// <summary>
    /// Walks up the visual tree (Y + TranslationY) to find how much of the
    /// ScrollView sits BELOW the visible screen edge. The player pane sizes its
    /// expanded zone taller than the screen and pushes it down, so the bottom of
    /// the lyrics viewport is clipped by the Border - centering must ignore that
    /// hidden strip, otherwise the active line appears too low. The Android
    /// navigation bar overlay is added on top of that.
    /// </summary>
    private double ComputeHiddenBottom()
    {
        double y = 0;
        VisualElement? current = TimedLyricsScrollView;
        VisualElement? root = null;

        while (current != null)
        {
            y += current.Y + current.TranslationY;
            root = current;
            current = current.Parent as VisualElement;
        }

        if (root == null || root.Height <= 0 || TimedLyricsScrollView.Height <= 0)
            return 0;

        double visibleFromScrollViewTop = root.Height - y;
        double hidden = Math.Max(0, TimedLyricsScrollView.Height - visibleFromScrollViewTop);

        // Navigation buttons / gesture pill overlay the bottom of the screen on
        // Android (edge-to-edge) - that strip is not usable for centering either.
        return hidden + LyricsPlatformInsets.GetBottomInsetDp(root.Height);
    }

    /// <summary>Center of the target label in ScrollView content coordinates.</summary>
    private double GetContentCenterY(Label label, int index)
    {
        double hostY = LyricsItemsHost.Y >= 0 ? LyricsItemsHost.Y : TopSpacer.HeightRequest;

        if (label.Height > 0)
            return hostY + label.Y + label.Height / 2;

        // Collapsed empty anchor line (IsVisible=False via the XAML DataTrigger):
        // its real laid-out height IS zero, so its center is its top edge.
        if (!label.IsVisible)
            return hostY;

        // Layout has not run yet - fall back to a uniform-height estimate
        return TopSpacer.HeightRequest + index * EstimatedLineHeight + EstimatedLineHeight / 2;
    }

    private bool IsInMiddleZone(Label label, int index)
    {
        double hidden = ComputeHiddenBottom();
        double visibleHeight = TimedLyricsScrollView.Height - hidden;
        if (visibleHeight <= 0)
            return false;

        double centerYOnScreen = GetContentCenterY(label, index) - TimedLyricsScrollView.ScrollY;
        return Math.Abs(centerYOnScreen - visibleHeight / 2) <= visibleHeight * MiddleZoneRatio;
    }

    /// <summary>
    /// Keeps the top/bottom padding in sync so the FIRST and LAST lines can be
    /// centered: top = HALF the visible viewport, bottom = top + the hidden
    /// bottom strip (Android nav-bar area / clipped zone padding). The bottom
    /// spacer must be LONGER in content coordinates because its lowest part is
    /// covered by the nav bar - with both spacers EQUAL the visible blank below
    /// the last line came out SHORTER than the one above the first line (the
    /// whole list looked shifted down). Quantized at 8dp - the old 40dp quantum
    /// is what used to round the inset up and make the bottom look 1-2 lines
    /// too long.
    /// </summary>
    private void UpdateSpacers()
    {
        if (TimedLyricsScrollView.Height <= 0)
            return;

        double hidden = ComputeHiddenBottom();
        double visible = Math.Max(0, TimedLyricsScrollView.Height - hidden);
        double half = Math.Max(0, Math.Round((visible / 2) / SpacerQuantum) * SpacerQuantum);
        double hiddenQ = Math.Max(0, Math.Round(hidden / SpacerQuantum) * SpacerQuantum);

        if (Math.Abs(TopSpacer.HeightRequest - half) > 0.5)
            TopSpacer.HeightRequest = half;

        double bottom = half + hiddenQ;
        if (Math.Abs(BottomSpacer.HeightRequest - bottom) > 0.5)
            BottomSpacer.HeightRequest = bottom;

        // VerticalOptions=Center centers inside THIS view's full height, whose
        // bottom edge can still sit under the Android nav bar (edge-to-edge) -
        // a bottom margin of that inset shifts the centered prompt back to the
        // true visible center.
        if (Math.Abs(NoLyricsPrompt.Margin.Bottom - hiddenQ) > 0.5)
            NoLyricsPrompt.Margin = new Thickness(0, 0, 0, hiddenQ);
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (double.IsNaN(Height) || Height <= 0)
            return;

        // The learned centering bias belongs to the geometry it was measured
        // in (Medium vs Full screen have different hidden-strip/inset errors).
        // Keeping it across a size change made the whole list sit visibly too
        // low or too high ("still offset") until the next lyric switch - drop
        // it here so the feedback loop re-learns the residual for THIS size.
        if (Math.Abs(Height - _lastBiasHeight) > 1)
        {
            _centerBiasDp = 0;
            _lastBiasHeight = Height;
        }

        EnsureItemsHooked();
        UpdateSpacers();

        // Scroll adjustments right after a layout change are NOT user scrolls
        _suppressUserScrollUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
    }

    // ========================= Auto-follow behavior =========================

    private void OnTimedLyricsScrolled(object? sender, ScrolledEventArgs e)
    {
        // Styling no longer depends on the scroll position (the fade is anchored
        // on the highlighted line, not on the viewport), so there is nothing to
        // recompute per scroll frame here.

        var now = DateTime.UtcNow;

        // Ignore our own programmatic scrolls and layout-driven adjustments
        if (now < _programmaticScrollUntil)
            return;
        if (now < _suppressUserScrollUntil)
            return;

        _lastUserScrollAt = now;

        if (_isFollowing)
        {
            // User took over: stop following and drop the depth styling
            _isFollowing = false;
            System.Diagnostics.Debug.WriteLine("[LyricsView] auto-follow OFF (user scrolled)");
            ApplyDistanceStyles();
        }
    }

    private async System.Threading.Tasks.Task ProgrammaticScrollAsync(double offset, bool animate)
    {
        // Window must cover the whole animation (MAUI's animated scroll runs
        // ~500ms) plus stray trailing Scrolled events, so none of them are
        // mistaken for user interaction. Unlike a counter it can never leak.
        _programmaticScrollUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(animate ? 900 : 200);
        try
        {
            await TimedLyricsScrollView.ScrollToAsync(0, offset, animate);
        }
        catch
        {
            // Ignore scroll races during layout/teardown
        }
        finally
        {
            // Keep ignoring events for a short buffer after the scroll settles.
            var settleBuffer = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
            if (settleBuffer > _programmaticScrollUntil)
                _programmaticScrollUntil = settleBuffer;
        }
    }

    // ====================== Measured centering feedback ======================

    /// <summary>
    /// Scrolls to the target, then MEASURES where the active line really ended
    /// up on screen (native view coordinates, dp). If it is off the ideal
    /// center by more than the tolerance, an instant corrective scroll snaps
    /// it into place and the measured residual is learned (_centerBiasDp) and
    /// pre-applied to every following centering target - so after the first
    /// correction, subsequent lyric switches land dead-center with no jump.
    /// This absorbs ANY constant geometry error instead of modeling it.
    /// </summary>
    private async System.Threading.Tasks.Task CenterWithFeedbackAsync(Label targetLabel, double targetOffset, bool animate, bool allowLearn)
    {
        await ProgrammaticScrollAsync(targetOffset + _centerBiasDp, animate);

        // One layout pass to settle. (The scroll await above has its own
        // try/catch, so it cannot throw; if the animated task never completes
        // the time-window guard still keeps everything else safe.)
        await System.Threading.Tasks.Task.Delay(animate ? 80 : 16);

        // The collapsed 0s anchor line (IsVisible=False via the XAML
        // DataTrigger) has no meaningful native geometry - Android reports
        // location 0,0 for GONE views - so measuring it would poison the
        // learned bias (a likely source of the intermittent "still offset"
        // look right after a track change). Estimate-only centering for it.
        if (!targetLabel.IsVisible)
            return;

        double? measured = GetOnScreenCenterYDp(targetLabel);
        double? ideal = GetIdealOnScreenCenterYDp();
        if (measured == null || ideal == null)
            return; // cannot measure (no handler yet / non-Android) - estimate-only centering

        // Ignore the sample if the active line changed while we were measuring.
        bool stillActive = targetLabel.BindingContext is TimedLyric tl && tl.IsActive;
        double delta = measured.Value - ideal.Value;

        System.Diagnostics.Debug.WriteLine(
            $"[LyricsView] feedback: measured={measured.Value:F1} ideal={ideal.Value:F1} delta={delta:F1} " +
            $"biasWas={_centerBiasDp:F1} learn={(allowLearn && stillActive ? 1 : 0)}");

        if (Math.Abs(delta) <= FeedbackToleranceDp)
            return;

        if (!allowLearn)
            return; // the line cannot reach the center (song start/end): the scroll
                    // already clamped, so a corrective pass is a guaranteed no-op

        if (stillActive)
            _centerBiasDp = Math.Clamp(_centerBiasDp + delta, -200, 200);

        // Instant corrective scroll: moving the content down by delta moves
        // the line up by delta, onto the exact center.
        await ProgrammaticScrollAsync(TimedLyricsScrollView.ScrollY + delta, animate: false);
    }

#if ANDROID
    /// <summary>Real on-screen Y (dp) of the label's center, measured from its native view.</summary>
    private double? GetOnScreenCenterYDp(Label label)
    {
        try
        {
            if (label.Handler?.PlatformView is not global::Android.Views.View view)
                return null;

            double density = view.Resources?.DisplayMetrics?.Density ?? 0;
            if (density <= 0)
                return null;

            int[] loc = new int[2];
            view.GetLocationOnScreen(loc);
            return (loc[1] + view.Height / 2.0) / density;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ideal on-screen Y (dp) for a centered line: the middle of the
    /// ScrollView's VISIBLE strip - from its real top on screen down to the
    /// window bottom, minus the navigation-bar area when the window extends
    /// under it. Measured in the same coordinate space as GetOnScreenCenterYDp.
    /// </summary>
    private double? GetIdealOnScreenCenterYDp()
    {
        try
        {
            if (TimedLyricsScrollView.Handler?.PlatformView is not global::Android.Views.View svView)
                return null;

            double density = svView.Resources?.DisplayMetrics?.Density ?? 0;
            if (density <= 0)
                return null;

            int[] loc = new int[2];
            svView.GetLocationOnScreen(loc);
            double svTop = loc[1] / density;

            var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
            if (decor == null)
                return null;

            int[] dloc = new int[2];
            decor.GetLocationOnScreen(dloc);
            double windowBottom = (dloc[1] + decor.Height) / density;

            double inset = LyricsPlatformInsets.GetBottomInsetDp(decor.Height / density);
            return svTop + (windowBottom - inset - svTop) / 2.0;
        }
        catch
        {
            return null;
        }
    }
#else
    private double? GetOnScreenCenterYDp(Label label) => null;
    private double? GetIdealOnScreenCenterYDp() => null;
#endif

    // ========================= Depth / distance styling =====================

    /// <summary>
    /// Applies the depth + highlight look in one pass. The effect is anchored
    /// on the ACTIVE line - each row's DISTANCE to it decides the styling -
    /// never on the viewport middle, so the look is identical wherever the
    /// list is scrolled. ONE fade for both modes: an illumination falloff
    /// (quadratic, inverse-square-like) - the highlighted line lights up the
    /// surrounding rows, the glow decays with the square of the normalized
    /// distance and reaches the minimum contrast at the SIXTH row (~5.5
    /// visible rows per side, so the 6th is the view edge). The highlighted
    /// line keeps FULL white/opacity in both modes (the min-contrast rule).
    /// The ONLY mode difference is the blur: while AUTO-FOLLOWING the rows
    /// around the active one are progressively blurred (barely / readable /
    /// more / nearly unreadable / smeared from the view edge outward); while
    /// BROWSING there is no blur.
    /// Styling is applied PER ROW inside a window around the active line, not
    /// to every element of the list - beyond the window every row sits at the
    /// same floor look anyway. This bounds the native work (a RenderEffect per
    /// far label caused multi-second lag spikes when opening lyrics). Styling
    /// only changes when the active line or the follow state changes, NOT on
    /// scroll - redundant native updates are skipped via _lastAppliedStyles.
    /// </summary>
    private void ApplyDistanceStyles(int activeIndex = -2)
    {
        if (activeIndex == -2)
            activeIndex = FindActiveIndex();

        Color activeColor = GetThemeColor("OnSurfaceInverse", Color.FromArgb("#F8F9FA"));
        Color mutedColor = GetThemeColor("MutedColor", Color.FromArgb("#989C9F"));
        Color bgColor = GetThemeColor("SurfaceCanvas", Color.FromArgb("#111214"));

        // ONE fade for both modes (user spec): an illumination falloff - the
        // highlighted line "lights up" the surrounding rows and the glow
        // decays with the SQUARE of the normalized distance (inverse-square
        // flavored), reaching the minimum contrast at the SIXTH line. There
        // are ~5.5 visible rows on each side, so the 6th is the view edge.
        // The old 3-line ramp collapsed far too early.
        const double FadeConvergeLines = 6.0;
        const double BlendCap = 0.75;     // how far the color melts into the bg
        const double OpacityFloor = 0.10; // minimum contrast of far rows

        // Styling is applied PER ROW inside a window around the active line,
        // not to every element of the list: beyond the window every row sits
        // at the same floor look anyway. This bounds the native work - a
        // RenderEffect per far label caused multi-second lag spikes on open.
        const int StyleWindowLines = 12;

        Color floorColor = BlendColors(mutedColor, bgColor, BlendCap);

        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Label label)
                continue;

            Color targetColor;
            double targetOpacity;
            double blur = 0;

            if (activeIndex < 0)
            {
                // Nothing active - plain neutral look everywhere
                targetColor = mutedColor;
                targetOpacity = 1;
            }
            else if (i == activeIndex)
            {
                // The highlighted line: FULL contrast in BOTH modes (the
                // min-contrast rule - it must pop against the faded rest)
                targetColor = activeColor;
                targetOpacity = 1;
            }
            else
            {
                int distance = Math.Abs(i - activeIndex);
                if (distance > StyleWindowLines)
                {
                    // Outside the window: uniform floor look, no blur (cheap
                    // cached path - keeps native RenderEffects bounded).
                    targetColor = floorColor;
                    targetOpacity = OpacityFloor;
                }
                else
                {
                    // distance 1 -> x = 0: the row right next to the active
                    // one keeps the normal inactive color. t reaches 1 at
                    // distance FadeConvergeLines (the 6th row - view edge).
                    double x = (distance - 1) / (FadeConvergeLines - 1);
                    double t = Math.Clamp(x * x, 0, 1);
                    targetColor = BlendColors(mutedColor, bgColor, BlendCap * t);
                    targetOpacity = 1.0 - (1.0 - OpacityFloor) * t;
                    if (_isFollowing)
                    {
                        // Per-row blur curve (user-tuned, one step softer than
                        // before): next to the highlight BARELY blurred, then
                        // a bit more but still READABLE, then more, then
                        // nearly unreadable - from the view edge outward the
                        // rows melt into the background with the fade.
                        blur = distance switch
                        {
                            1 => 1.0,
                            2 => 2.0,
                            3 => 5.0,
                            4 => 11.0,
                            _ => 14.0
                        };
                    }
                }
            }

            // Skip redundant native updates.
            if (_lastAppliedStyles.TryGetValue(label, out var last) &&
                last.Color == targetColor && Math.Abs(last.Opacity - targetOpacity) < 0.005)
            {
                SetBlur(label, blur); // LyricsBlurPlatform no-ops unchanged values
                continue;
            }

            label.TextColor = targetColor;
            label.Opacity = targetOpacity;
            SetBlur(label, blur);
            _lastAppliedStyles[label] = (targetColor, targetOpacity);
        }
    }

    /// <summary>
    /// Real per-line blur via LyricsBlurPlatform (Android: GPU RenderEffect).
    /// Deliberately NOT a MAUI Effect - those need ConfigureEffects
    /// registration in MauiProgram.cs and fail silently/crash without it.
    /// The platform helper reaches the native view through the label's
    /// handler directly: zero registration, and the attached property ignores
    /// unchanged values, so the native blur is only re-applied when the
    /// radius truly changes (a few GPU work submissions per lyric switch).
    /// </summary>
    private void SetBlur(Label label, double radius)
    {
        LyricsBlurPlatform.SetRadius(label, radius);
    }

    private int FindActiveIndex()
    {
        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is Label lbl && lbl.BindingContext is TimedLyric lyric && lyric.IsActive)
                return i;
        }
        return -1;
    }

    private TimedLyric? FindActiveLyric()
    {
        foreach (var child in LyricsItemsHost.Children)
            if (child is Label lbl && lbl.BindingContext is TimedLyric lyric && lyric.IsActive)
                return lyric;
        return null;
    }

    private static Color BlendColors(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgba(
            from.Red + (to.Red - from.Red) * amount,
            from.Green + (to.Green - from.Green) * amount,
            from.Blue + (to.Blue - from.Blue) * amount,
            from.Alpha + (to.Alpha - from.Alpha) * amount);
    }

    private Color GetThemeColor(string key, Color fallback)
    {
        if (Resources.TryGetValue(key, out object? local) && local is Color localColor)
            return localColor;
        if (Application.Current?.Resources.TryGetValue(key, out object? app) == true && app is Color appColor)
            return appColor;
        return fallback;
    }

    // ========================= Tap to seek & item hooks =====================

    private void OnLyricLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Label label || label.BindingContext is not TimedLyric lyric)
            return;

        // Tapping a line expresses intent to jump there - always follow from now on
        _isFollowing = true;
        System.Diagnostics.Debug.WriteLine("[LyricsView] auto-follow ON (lyric line tapped)");
        ApplyDistanceStyles();

        LyricTapped?.Invoke(this, lyric);
    }

    /// <summary>
    /// The ViewModel rebuilds ActiveTimedLyrics (Clear + Add) into the SAME
    /// ObservableCollection instance, so the generated labels are recreated with
    /// the neutral style - re-apply the depth styling after each change.
    /// </summary>
    private void EnsureItemsHooked()
    {
        // BindableLayout.ItemsSource is an ATTACHED property - it has no instance
        // getter, so it must be read through BindableLayout.GetItemsSource().
        var current = BindableLayout.GetItemsSource(LyricsItemsHost) as INotifyCollectionChanged;
        if (ReferenceEquals(current, _hookedCollection))
            return; // already hooked (or nothing bound yet)

        if (_hookedCollection != null)
            _hookedCollection.CollectionChanged -= OnLyricsCollectionChanged;

        _hookedCollection = current;
        if (_hookedCollection != null)
            _hookedCollection.CollectionChanged += OnLyricsCollectionChanged;
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != BindableLayout.ItemsSourceProperty.PropertyName)
            return;

        // A new / late-bound collection arrived (e.g. ActiveTimedLyrics assigned
        // after the BindingContext was inherited, or a new track's list) - hook
        // it, treat it as a track change, and restyle the fresh labels.
        EnsureItemsHooked();
        NotifyTrackChanged();
        ScheduleDistanceStyles(clearCache: true); // fresh labels incoming
    }

    private void OnLyricsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A Reset means the whole lyric list was rebuilt - i.e. the track (or
        // its lyrics) changed. Always resume following and scroll to the start
        // of the new list, even if the user was browsing the previous track.
        if (e.Action == NotifyCollectionChangedAction.Reset)
            NotifyTrackChanged();

        ScheduleDistanceStyles(clearCache: e.Action == NotifyCollectionChangedAction.Reset);
    }

    /// <summary>
    /// Call when the track (or its lyrics) changed: unconditionally resumes
    /// auto-follow and scrolls to the start of the new list - normally the
    /// empty 0-second anchor line - once it has been laid out.
    /// </summary>
    public void NotifyTrackChanged()
    {
        _isFollowing = true;
        _lastUserScrollAt = DateTime.MinValue;
        System.Diagnostics.Debug.WriteLine("[LyricsView] auto-follow ON (track changed)");
        ApplyDistanceStyles();
        StartPendingStartScroll();
    }

    /// <summary>
    /// Retries the "scroll to start" until the rebuilt list has an active line
    /// and a measurable viewport - the request usually arrives BEFORE the first
    /// layout pass of the freshly created labels, so a single immediate attempt
    /// would silently do nothing and the highlighted line would stay offscreen.
    /// </summary>
    private void StartPendingStartScroll()
    {
        _pendingStartScroll = true;
        _startScrollAttempts = 12; // ~2s at 160ms

        if (_startScrollTimerRunning)
            return;
        _startScrollTimerRunning = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(160), () =>
        {
            bool keepGoing = PendingStartScrollTick();
            if (!keepGoing)
                _startScrollTimerRunning = false;
            return keepGoing;
        });
    }

    private bool PendingStartScrollTick()
    {
        if (!_pendingStartScroll)
            return false;
        if (_startScrollAttempts-- <= 0)
        {
            _pendingStartScroll = false;
            return false;
        }
        if (TimedLyricsScrollView.Height <= 0)
            return true; // control not measured yet - keep waiting

        var active = FindActiveLyric();
        _pendingStartScroll = false;

        if (active != null)
            ScrollToLyric(active, animate: true, allowResume: true);
        else
            _ = ScrollToStartAndRestyleAsync(); // no anchor line yet - go to the very start

        return false;
    }

    private async System.Threading.Tasks.Task ScrollToStartAndRestyleAsync()
    {
        await ProgrammaticScrollAsync(0, animate: true);
        ApplyDistanceStyles();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        EnsureItemsHooked();
        ScheduleDistanceStyles(clearCache: true);
    }
}

/// <summary>
/// Bottom system-bar (navigation buttons / gesture pill) inset in
/// device-independent pixels. 0 when the window does not extend under the
/// system bars (i.e. the system already reserved them outside the window).
/// </summary>
internal static class LyricsPlatformInsets
{
    public static double GetBottomInsetDp(double rootHeightDp)
    {
#if ANDROID
        try
        {
            if (rootHeightDp <= 0)
                return 0;

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            var insets = activity?.Window?.DecorView?.RootWindowInsets;
            if (insets is null)
                return 0;

            int bottomPx = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsetsIgnoringVisibility(Android.Views.WindowInsets.Type.SystemBars());
                bottomPx = bars?.Bottom ?? 0;
            }

            if (bottomPx <= 0)
                return 0;

            // Only subtract when the page really extends under the system bars
            // (edge-to-edge). If the window is shorter than the screen, the bars
            // are already reserved OUTSIDE of it and must not be subtracted.
            var display = Microsoft.Maui.Devices.DeviceDisplay.MainDisplayInfo;
            double density = display.Density <= 0 ? 1 : display.Density;
            double screenHdp = display.Height / density;
            if (rootHeightDp < screenHdp - 2)
                return 0;

            return bottomPx / density;
        }
        catch
        {
            return 0;
        }
#else
        return 0;
#endif
    }
}
