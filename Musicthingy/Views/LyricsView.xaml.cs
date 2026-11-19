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
/// - Depth effect is anchored on the HIGHLIGHTED line: the same illumination
///   falloff (quadratic, inverse-square-like) in BOTH modes - the highlighted
///   line lights up the surrounding rows and reaches its minimum at the SIXTH
///   line (~5.5 visible rows per side, so the 6th is right at the view edge).
///   While BROWSING the minimum contrast floor ALWAYS stays readable (opacity
///   0.55, color only 40% into the background) so the lyrics can still be
///   read; while FOLLOWING the fade goes deep (floor 0.10) and the rows
///   around the highlight are progressively blurred (barely / readable /
///   more / nearly unreadable / smeared at the edge).
/// - Effects are applied PER WRAPPED ROW: a line too long for the viewport
///   wraps into several visual rows, and the code-behind splits it into one
///   Label per visual row (Android text measurement, matching the native
///   line breaks). Every row's PHYSICAL distance to the highlighted block
///   then decides its blur/fade, so a wrapped line reads as several separate
///   elements - the rows of a long line darken and blur row by row.
///   Styling runs in a window around the active line, restyles are coalesced
///   into one pass, and redundant native updates are skipped - all to keep
///   the native work bounded (per-label RenderEffect churn caused
///   multi-second lag spikes when opening lyrics).
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

    // During the resize animation the exact padding is invisible, so use a
    // COARSE quantum: the spacer values then change ~4x less often and the
    // lyric host is not re-measured on nearly every frame. FinalizeVisibility
    // / OnSizeChanged snap back to the exact quantum afterwards, so no dead
    // padding is left behind.
    private const double AnimationSpacerQuantum = 32;

    // Auto-follow resume rules (per spec)
    private const double AutoFollowResumeDelaySeconds = 1.5;
    private const double AutoFollowResumeForcedSeconds = 4.0; // idle after which following resumes from ANY position
    private const double MiddleZoneRatio = 0.25;   // "general area of the middle" = middle 50% of the viewport
    private const double FeedbackToleranceDp = 8;  // measured centering error below this is left alone

    // Deepest blur radius (dp) of the depth ladder - also used for rows
    // outside the styling window so the blur never snaps back to sharp.
    private const double MaxBlurDp = 14.0;

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

    // Sequence id of the measured-centering pass. Every new centering request
    // and every user scroll bumps it, so a feedback pass that was still
    // measuring can never apply a stale corrective scroll (which used to yank
    // the list back to a line that was no longer active, or undo a scroll the
    // user had just started).
    private int _centerFeedbackId;

    // Height at which _centerBiasDp was (re)learned. The residual differs per
    // geometry (Medium vs Full screen), so the bias is dropped on size changes
    // - keeping it made the whole list sit visibly too low/high ("offset").
    private double _lastBiasHeight;

    // Last color/opacity applied to each row label - styling re-runs whenever
    // the active line or the follow state changes, so redundant native
    // property sets are skipped (labels are recreated on every track change,
    // so the cache is cleared there).
    private readonly Dictionary<Label, (Color Color, double Opacity)> _lastAppliedStyles = new();

    // Per-wrapped-row splitting: attached state on each line block recording
    // the label width (dp) at which its row structure was last computed.
    // -1 = not measured yet. The state lives ON the block, so there is
    // nothing to clear on track changes (fresh blocks come with fresh state).
    private static readonly BindableProperty SplitWidthProperty =
        BindableProperty.CreateAttached("SplitWidth", typeof(double), typeof(LyricsView), -1.0);

    // Row-splitting measurement retries: the native views may not exist yet
    // on the first passes after a track change. Refreshed on track/size
    // changes so a retry budget exhausted while hidden is restored.
    private int _splitRetriesLeft = 20;
    private bool _splitRetryQueued;

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
            EnsureRowSplit(); // split wrapped lines into per-row labels first
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
        VerticalStackLayout? targetBlock = null;
        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is VerticalStackLayout block && ReferenceEquals(block.BindingContext, lyric))
            {
                targetBlock = block;
                index = i;
                break;
            }
        }

        if (targetBlock == null)
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
            bool inMiddle = IsInMiddleZone(targetBlock, index);
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

        double unclampedOffset = GetContentCenterY(targetBlock, index) - visibleHeight / 2;
        double maxOffset = Math.Max(0, TimedLyricsScrollView.ContentSize.Height - TimedLyricsScrollView.Height);
        double targetOffset = Math.Clamp(unclampedOffset, 0, maxOffset);
        bool clamped = Math.Abs(unclampedOffset - targetOffset) > 0.5; // line cannot reach center (song start/end)

        // Centering diagnostics - the "feedback" line that follows reports the
        // REAL measured position and any residual bias.
        System.Diagnostics.Debug.WriteLine(
            $"[LyricsView] center: idx={index} lineCenterY={GetContentCenterY(targetBlock, index):F1} " +
            $"viewportH={TimedLyricsScrollView.Height:F1} contentH={TimedLyricsScrollView.ContentSize.Height:F1} " +
            $"hiddenBottom={hidden:F1} visibleH={visibleHeight:F1} target={targetOffset + _centerBiasDp:F1} " +
            $"(raw {unclampedOffset:F1}, max {maxOffset:F1}, bias {_centerBiasDp:F1})");

        // Scroll, then MEASURE the line's real on-screen position, snap it to
        // the true center and learn the residual bias for next time.
        int feedbackId = ++_centerFeedbackId;
        _ = CenterWithFeedbackAsync(targetBlock, targetOffset, animate, allowLearn: !clamped, feedbackId);
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
            if (children[i] is VerticalStackLayout block && block.BindingContext is TimedLyric lyric && lyric.IsActive)
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

        // The pane is about to grow into view. Restore the wrapped-row
        // measurement budget that a collapsed control (zero width, nothing
        // measurable) may have burned through while it was hidden.
        _splitRetriesLeft = 20;
    }

    /// <summary>
    /// Restores final IsVisible states once a resize animation has fully completed.
    /// </summary>
    public void FinalizeVisibility()
    {
        NoLyricsPrompt.IsVisible = NoLyricsPrompt.Opacity > 0.01;

        // The animation used the coarse spacer quantum; snap to the exact
        // values now that the geometry has settled.
        UpdateSpacers();
    }

    /// <summary>
    /// Fades the "no lyrics" prompt in as the player approaches full screen and
    /// keeps the half-viewport paddings in sync while the geometry changes.
    /// </summary>
    public void UpdateForFullScreenProgress(double progressToFull)
    {
        NoLyricsPrompt.Opacity = Math.Clamp(progressToFull * 2, 0, 1);
        EnsureItemsHooked();
        UpdateSpacers(coarse: true);
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
        return hidden + LyricsPlatformInsets.GetBottomInsetDp(root);
    }

    /// <summary>Center of the target line block in ScrollView content coordinates.</summary>
    private double GetContentCenterY(VerticalStackLayout block, int index)
    {
        double hostY = LyricsItemsHost.Y >= 0 ? LyricsItemsHost.Y : TopSpacer.HeightRequest;

        if (block.Height > 0)
            return hostY + block.Y + block.Height / 2;

        // Collapsed empty anchor line (IsVisible=False via the XAML DataTrigger):
        // its real laid-out height IS zero, so its center is its top edge.
        if (!block.IsVisible)
            return hostY;

        // Layout has not run yet - fall back to a uniform-height estimate
        return TopSpacer.HeightRequest + index * EstimatedLineHeight + EstimatedLineHeight / 2;
    }

    private bool IsInMiddleZone(VerticalStackLayout block, int index)
    {
        double hidden = ComputeHiddenBottom();
        double visibleHeight = TimedLyricsScrollView.Height - hidden;
        if (visibleHeight <= 0)
            return false;

        double centerYOnScreen = GetContentCenterY(block, index) - TimedLyricsScrollView.ScrollY;
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
    private void UpdateSpacers(bool coarse = false)
    {
        if (TimedLyricsScrollView.Height <= 0)
            return;

        double quantum = coarse ? AnimationSpacerQuantum : SpacerQuantum;
        double hidden = ComputeHiddenBottom();
        double visible = Math.Max(0, TimedLyricsScrollView.Height - hidden);
        double half = Math.Max(0, Math.Round((visible / 2) / quantum) * quantum);
        double hiddenQ = Math.Max(0, Math.Round(hidden / quantum) * quantum);

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

        // A size change can re-wrap every line - refresh the per-wrapped-row
        // split (a cheap no-op when the width did not change) and refresh the
        // measurement retry budget.
        _splitRetriesLeft = 20;
        EnsureRowSplit();

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
            // Any centering pass still in flight must not snap the list back.
            _centerFeedbackId++;
            System.Diagnostics.Debug.WriteLine("[LyricsView] auto-follow OFF (user scrolled)");
            ApplyDistanceStyles();
        }
    }

    private async System.Threading.Tasks.Task ProgrammaticScrollAsync(double offset, bool animate)
    {
        // Window must cover the whole animation (MAUI's animated scroll runs
        // ~500ms) plus stray trailing Scrolled events, so none of them are
        // mistaken for user interaction. Unlike a counter it can never leak.
        // Kept as short as the animation allows: every millisecond here is a
        // millisecond in which a real user scroll is swallowed, and in songs
        // with fast lines that felt like the list refusing to be grabbed.
        _programmaticScrollUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(animate ? 700 : 200);
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
            var settleBuffer = DateTime.UtcNow + TimeSpan.FromMilliseconds(120);
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
    private async System.Threading.Tasks.Task CenterWithFeedbackAsync(VerticalStackLayout targetBlock, double targetOffset, bool animate, bool allowLearn, int feedbackId)
    {
        await ProgrammaticScrollAsync(targetOffset + _centerBiasDp, animate);

        // One layout pass to settle. (The scroll await above has its own
        // try/catch, so it cannot throw; if the animated task never completes
        // the time-window guard still keeps everything else safe.)
        await System.Threading.Tasks.Task.Delay(animate ? 80 : 16);

        // A newer centering request, or the user taking over the scroll, makes
        // this pass obsolete BEFORE it measures anything - never move the list
        // on behalf of a line that is no longer active.
        if (feedbackId != _centerFeedbackId || !_isFollowing)
            return;

        // The collapsed 0s anchor line (IsVisible=False via the XAML
        // DataTrigger) has no meaningful native geometry - Android reports
        // location 0,0 for GONE views - so measuring it would poison the
        // learned bias (a likely source of the intermittent "still offset"
        // look right after a track change). Estimate-only centering for it.
        if (!targetBlock.IsVisible)
            return;

        double? measured = GetOnScreenCenterYDp(targetBlock);
        double? ideal = GetIdealOnScreenCenterYDp();
        if (measured == null || ideal == null)
            return; // cannot measure (no handler yet / non-Android) - estimate-only centering

        // Ignore the sample if the active line changed while we were measuring.
        bool stillActive = targetBlock.BindingContext is TimedLyric tl && tl.IsActive;
        double delta = measured.Value - ideal.Value;

        System.Diagnostics.Debug.WriteLine(
            $"[LyricsView] feedback: measured={measured.Value:F1} ideal={ideal.Value:F1} delta={delta:F1} " +
            $"biasWas={_centerBiasDp:F1} learn={(allowLearn && stillActive ? 1 : 0)}");

        if (Math.Abs(delta) <= FeedbackToleranceDp)
            return;

        if (!allowLearn || !stillActive)
            return; // line cannot reach the center, or it is no longer active

        _centerBiasDp = Math.Clamp(_centerBiasDp + delta, -200, 200);

        // Instant corrective scroll: moving the content down by delta moves
        // the line up by delta, onto the exact center. Only while this pass is
        // STILL the newest one - otherwise it would fight the next transition.
        if (feedbackId != _centerFeedbackId || !_isFollowing)
            return;

        await ProgrammaticScrollAsync(TimedLyricsScrollView.ScrollY + delta, animate: false);
    }

#if ANDROID
    /// <summary>Real on-screen Y (dp) of an element's center, measured from its native view.</summary>
    private double? GetOnScreenCenterYDp(VisualElement element)
    {
        try
        {
            if (element.Handler?.PlatformView is not global::Android.Views.View view)
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

            double inset = LyricsPlatformInsets.GetBottomInsetDp(TimedLyricsScrollView);

            // The visible strip ends at the EARLIER of the window bottom (minus
            // the nav bar) and the ScrollView's own bottom edge. Assuming the
            // ScrollView always reaches the screen bottom pulled the "ideal"
            // center downward whenever a mini player / transport bar sat below
            // it, so the learned bias chased a line that was never centered.
            double svBottom = (loc[1] + svView.Height) / density;
            double visibleBottom = Math.Min(svBottom, windowBottom - inset);
            if (visibleBottom <= svTop)
                return null;

            return svTop + (visibleBottom - svTop) / 2.0;
        }
        catch
        {
            return null;
        }
    }
#else
    private double? GetOnScreenCenterYDp(VisualElement element) => null;
    private double? GetIdealOnScreenCenterYDp() => null;
#endif

    // ========================= Depth / distance styling =====================

    /// <summary>
    /// Applies the depth + highlight look in one pass. The effect is anchored
    /// on the ACTIVE line - each row's DISTANCE to it decides the styling -
    /// never on the viewport middle, so the look is identical wherever the
    /// list is scrolled. ONE fade shape for both modes: an illumination
    /// falloff (quadratic, inverse-square-like) - the highlighted line lights
    /// up the surrounding rows and the glow reaches its minimum at the SIXTH
    /// line. The ONLY mode difference is how deep the fade may go: while
    /// BROWSING the minimum contrast floor ALWAYS stays readable (the far
    /// text must remain legible), while FOLLOWING the fade can go to the
    /// full depth and the rows around the highlight are progressively
    /// blurred.
    /// Distance is PHYSICAL (in line pitches, to the nearest row of the
    /// highlighted block), so a long line that wraps into several visual
    /// rows - split into one Label per row by EnsureRowSplit - is styled row
    /// by row: every wrapped row behaves as its own element.
    /// Styling only touches a window around the active line; beyond it every
    /// row sits at the same floor look anyway. Redundant native updates are
    /// skipped via _lastAppliedStyles and restyles are coalesced - together
    /// these keep the native work bounded (per-label RenderEffect churn used
    /// to cause multi-second lag spikes when opening lyrics).
    /// </summary>
    private void ApplyDistanceStyles(int activeIndex = -2)
    {
        if (activeIndex == -2)
            activeIndex = FindActiveIndex();

        Color activeColor = GetThemeColor("OnSurfaceInverse", Color.FromArgb("#F8F9FA"));
        Color mutedColor = GetThemeColor("MutedColor", Color.FromArgb("#989C9F"));
        Color bgColor = GetThemeColor("SurfaceCanvas", Color.FromArgb("#111214"));

        // ONE fade shape for both modes: quadratic (inverse-square-like)
        // illumination falloff, minimum contrast reached exactly at the 6th
        // line away from the highlight.
        const double FadeReachLines = 6.0;

        // The mode difference (user rule): while BROWSING the minimum contrast
        // ALWAYS stays readable - the far lyrics must still be legible. While
        // FOLLOWING the fade goes to full depth.
        double blendCap = _isFollowing ? 0.75 : 0.40;
        double opacityFloor = _isFollowing ? 0.10 : 0.55;
        Color floorColor = BlendColors(mutedColor, bgColor, blendCap);

        // Window (in line slots) inside which rows get per-distance styling;
        // beyond it every row sits at the same floor look anyway. Bounds the
        // work per pass.
        const int StyleWindowLines = 12;

        double pitch = GetLinePitch();

        var children = LyricsItemsHost.Children;

        // Absolute Y (content coordinates) of every row center of the
        // highlighted block. With these, distance is PHYSICAL - measured in
        // line pitches to the nearest highlighted row - so the rows of a
        // wrapped line fade/blur row by row (each wrapped row behaves as its
        // own element). Null until the layout has real geometry; styling then
        // falls back to whole-line distances.
        List<double>? activeRowCenters = null;
        if (activeIndex >= 0 && activeIndex < children.Count &&
            children[activeIndex] is VerticalStackLayout activeBlock &&
            activeBlock.IsVisible && activeBlock.Height > 0 && pitch > 0)
        {
            activeRowCenters = new List<double>(activeBlock.Children.Count);
            foreach (var child in activeBlock.Children)
                if (child is Label activeRow && activeRow.Height > 0)
                    activeRowCenters.Add(activeBlock.Y + activeRow.Y + activeRow.Height / 2.0);
            if (activeRowCenters.Count == 0)
                activeRowCenters = null;
        }

        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not VerticalStackLayout block)
                continue;
            if (!block.IsVisible)
                continue; // collapsed 0s anchor line

            bool isActive = i == activeIndex;

            foreach (var child in block.Children)
            {
                if (child is not Label row)
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
                else if (isActive)
                {
                    // The highlighted block (ALL of its rows): FULL contrast
                    // in both modes (the min-contrast rule - it must pop
                    // against the faded rest)
                    targetColor = activeColor;
                    targetOpacity = 1;
                }
                else
                {
                    int indexGap = Math.Abs(i - activeIndex);
                    if (indexGap > StyleWindowLines)
                    {
                        // Outside the window: uniform floor look, with the
                        // DEEPEST blur while following. Blur must not snap back
                        // to 0 here - on a tall screen (>window height) or at
                        // the clamped start of a song that rendered sharp rows
                        // below heavily blurred ones.
                        targetColor = floorColor;
                        targetOpacity = opacityFloor;
                        blur = _isFollowing ? MaxBlurDp : 0;
                    }
                    else
                    {
                        double dLines;
                        if (activeRowCenters != null && row.Height > 0)
                        {
                            double rowCenter = block.Y + row.Y + row.Height / 2.0;
                            double nearest = double.MaxValue;
                            foreach (double c in activeRowCenters)
                            {
                                double dy = Math.Abs(rowCenter - c);
                                if (dy < nearest)
                                    nearest = dy;
                            }
                            dLines = nearest / pitch;
                        }
                        else
                        {
                            dLines = indexGap; // no real geometry yet - line distance
                        }

                        if (dLines < 1)
                            dLines = 1; // closer than one pitch: base inactive look

                        // distance 1 -> t = 0: the row right next to the
                        // highlight keeps the normal inactive color. t reaches
                        // 1 at FadeReachLines (the 6th line - view edge) and
                        // grows quadratically: the falloff is gentle near the
                        // highlight (illumination) and steeper far away.
                        double x = (dLines - 1) / (FadeReachLines - 1);
                        double t = Math.Clamp(x, 0, 1);
                        t *= t;
                        targetColor = BlendColors(mutedColor, bgColor, blendCap * t);
                        targetOpacity = 1.0 - (1.0 - opacityFloor) * t;

                        if (_isFollowing)
                            blur = LerpBlur(dLines);
                    }
                }

                // Skip redundant native updates.
                if (_lastAppliedStyles.TryGetValue(row, out var last) &&
                    last.Color == targetColor && Math.Abs(last.Opacity - targetOpacity) < 0.005)
                {
                    SetBlur(row, blur); // LyricsBlurPlatform no-ops unchanged values
                    continue;
                }

                row.TextColor = targetColor;
                row.Opacity = targetOpacity;
                SetBlur(row, blur);
                _lastAppliedStyles[row] = (targetColor, targetOpacity);
            }
        }
    }

    /// <summary>
    /// Pitch of ONE lyric line (block height + its vertical margins),
    /// averaged over the first few laid-out SINGLE-ROW lines. Wrapped blocks
    /// are skipped so long lines cannot inflate the average. This pitch is
    /// the unit the illumination falloff is measured in ("the 6th line").
    /// </summary>
    private double GetLinePitch()
    {
        double sum = 0;
        int n = 0;
        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count && n < 8; i++)
        {
            if (children[i] is VerticalStackLayout block && block.IsVisible &&
                block.Height > 0 && block.Children.Count == 1 && block.Children[0] is Label)
            {
                sum += block.Height + block.Margin.VerticalThickness;
                n++;
            }
        }

        double pitch = n > 0 ? sum / n : EstimatedLineHeight;
        return pitch > 0 ? pitch : EstimatedLineHeight;
    }

    /// <summary>
    /// Blur radius (dp) for a row at the given line distance. User-tuned
    /// ladder, one tier softer than the previous build: the row right next to
    /// the highlight is BARELY blurred, the second readable, the third more,
    /// the fourth nearly unreadable - from the fifth onward the rows melt
    /// into the background with the fade. Fractional distances (wrapped rows)
    /// interpolate between the tiers.
    /// </summary>
    private static double LerpBlur(double dLines)
    {
        if (dLines <= 1)
            return 0.3;
        if (dLines >= 5)
            return MaxBlurDp;

        double[] tierAt = { 1, 2, 3, 4, 5 };
        double[] blurAt = { 0.3, 2.0, 5.0, 11.0, MaxBlurDp };
        for (int i = 1; i < tierAt.Length; i++)
        {
            if (dLines <= tierAt[i])
            {
                double f = (dLines - tierAt[i - 1]) / (tierAt[i] - tierAt[i - 1]);
                return blurAt[i - 1] + (blurAt[i] - blurAt[i - 1]) * f;
            }
        }
        return MaxBlurDp;
    }

    // ======================== Per-wrapped-row splitting ======================

    /// <summary>
    /// Splits lyric lines that WRAP into one Label per visual row, so the
    /// depth effects treat every wrapped row as its own element (blur/fade
    /// escalate down the rows of a long line - see ApplyDistanceStyles).
    /// Safe to call at any time and cheap when nothing changed: blocks whose
    /// row structure was already computed at the current width are skipped in
    /// O(1). Re-runs automatically when the width changes (re-wrap) and when
    /// the native views were not ready yet (bounded retries).
    /// Non-Android platforms keep whole lines (no split support there - the
    /// effects then style per line, as before).
    /// </summary>
    private void EnsureRowSplit()
    {
        bool anySplit = false;
        bool anyPending = false;
        int measurements = 0;

        var children = LyricsItemsHost.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not VerticalStackLayout block || !block.IsVisible)
                continue;
            if (block.BindingContext is not TimedLyric lyric || string.IsNullOrEmpty(lyric.Text))
                continue;

            Label? measureLabel = null;
            foreach (var child in block.Children)
                if (child is Label l) { measureLabel = l; break; }
            if (measureLabel == null)
                continue;

            double labelWidth = measureLabel.Width; // dp - the label itself has no margin
            if (labelWidth <= 0)
            {
                anyPending = true; // not laid out yet
                continue;
            }

            double doneAt = (double)block.GetValue(SplitWidthProperty);
            if (Math.Abs(doneAt - labelWidth) <= 0.5)
                continue; // row structure already correct for this width

            if (++measurements > 80)
            {
                // Bound the work per pass; the remainder continues on the
                // next retry.
                anyPending = true;
                break;
            }

            List<string>? segments = TryMeasureWrappedRows(measureLabel, lyric.Text, labelWidth);
            if (segments == null)
            {
                anyPending = true; // native view not ready yet - retry shortly
                continue;
            }

            block.SetValue(SplitWidthProperty, labelWidth);

            if (segments.Count <= 1)
            {
                // The line fits on ONE row at this width. If it was split at a
                // narrower width (rotation / resize) the stale truncated row
                // labels must be merged back into the single full line -
                // otherwise the block stayed broken across several rows
                // forever. Rebuild from lyric.Text, not from the first segment
                // (which is all the existing label still holds).
                if (block.Children.Count > 1)
                {
                    ReplaceBlockRows(block, new List<string> { lyric.Text }, measureLabel, lyric);
                    anySplit = true;
                }
                continue;
            }

            // Replace the single template label with one label per visual row.
            ReplaceBlockRows(block, segments, measureLabel, lyric);
            anySplit = true;
        }

        if (anySplit)
        {
            // The rows are brand-new elements - drop stale cache entries and
            // restyle them, then refine once they have real geometry.
            _lastAppliedStyles.Clear();
            ScheduleDistanceStyles(clearCache: false);
            // NOTE: ApplyDistanceStyles has an optional parameter, and C#
            // method-group conversions don't honor optional parameters - so it
            // must be wrapped in a lambda to bind to System.Action.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () => ApplyDistanceStyles());
        }

        if (anyPending && !_splitRetryQueued && _splitRetriesLeft > 0)
        {
            _splitRetryQueued = true;
            _splitRetriesLeft--;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
            {
                _splitRetryQueued = false;
                EnsureRowSplit();
            });
        }
    }

    /// <summary>
    /// Measures where the platform text layout breaks <paramref name="text"/>
    /// into visual rows at the given width, using the label's own native text
    /// paint so the measured breaks match the real rendering exactly.
    /// Returns null when measurement is not possible yet (no native view).
    /// A list with one entry means the text fits on a single row.
    /// </summary>
    private List<string>? TryMeasureWrappedRows(Label label, string text, double widthDp)
    {
#if ANDROID
        try
        {
            if (label.Handler?.PlatformView is not global::Android.Widget.TextView tv)
                return null;

            double density = tv.Resources?.DisplayMetrics?.Density ?? 0;
            if (density <= 0)
                return null;

            // Available text width in px: prefer the native view's real
            // content width; fall back to the MAUI width via the density.
            int availablePx;
            if (tv.Width > 0)
                availablePx = tv.Width - tv.TotalPaddingLeft - tv.TotalPaddingRight;
            else
                availablePx = (int)(widthDp * density);
            if (availablePx <= 0)
                return null;

            // TextView exposes its text paint as Paint (typed TextPaint in the
            // Android binding) - copy it so StaticLayout measurement uses the
            // exact same typeface, size and styling as the real rendering.
            // Both are native (Java) objects: dispose them explicitly, or up to
            // 80 peers per pass would linger until finalization.
            using var paint = new global::Android.Text.TextPaint(tv.Paint);

            // Same paint, width and break strategy as the real TextView - the
            // measured line breaks match what the user sees. (StaticLayout is
            // an IDisposable Java peer as well.)
            using global::Android.Text.StaticLayout layout = OperatingSystem.IsAndroidVersionAtLeast(23)
                ? global::Android.Text.StaticLayout.Builder.Obtain(text, 0, text.Length, paint, availablePx)
                    .SetBreakStrategy(tv.BreakStrategy)
                    .SetHyphenationFrequency(tv.HyphenationFrequency)
                    .SetIncludePad(false)
                    .Build()
                : new global::Android.Text.StaticLayout(text, paint, availablePx,
                    global::Android.Text.Layout.Alignment.AlignCenter, 1f, 0f, false);

            int lines = layout.LineCount;
            var segments = new List<string>(lines);
            for (int l = 0; l < lines; l++)
            {
                int start = layout.GetLineStart(l);
                int end = layout.GetLineEnd(l);
                string segment = text.Substring(start, end - start).TrimEnd();
                if (segment.Length > 0)
                    segments.Add(segment);
            }

            return segments.Count > 0 ? segments : new List<string> { text };
        }
        catch
        {
            return null;
        }
#else
        // Per-wrapped-row splitting is implemented for Android; other
        // platforms keep the whole line as one element (the effects then
        // style per line, as before).
        return new List<string>();
#endif
    }

    /// <summary>
    /// Replaces the block's single template label with one label per wrapped
    /// row. Goes through the Core ILayout : IList&lt;IView&gt; surface, whose
    /// shape is stable across MAUI versions (Layout&lt;T&gt;.Children mutability
    /// differs between them).
    /// </summary>
    private void ReplaceBlockRows(VerticalStackLayout block, List<string> segments, Label template, TimedLyric lyric)
    {
        var viewList = (System.Collections.Generic.IList<global::Microsoft.Maui.IView>)block;
        viewList.Clear();
        foreach (string segment in segments)
            viewList.Add(CreateRowLabel(segment, template, lyric));
    }

    /// <summary>
    /// A per-wrapped-row label: identical typography to the template label
    /// (so row heights and centering stay uniform), no margins (the line
    /// block carries them), and the same tap-to-seek gesture.
    /// </summary>
    private Label CreateRowLabel(string segment, Label template, TimedLyric lyric)
    {
        var row = new Label
        {
            Text = segment,
            BindingContext = lyric,
            FontAttributes = template.FontAttributes,
            FontFamily = template.FontFamily,
            FontSize = template.FontSize,
            LineHeight = template.LineHeight,
            HorizontalOptions = LayoutOptions.Fill,
            HorizontalTextAlignment = template.HorizontalTextAlignment,
            // Safety net: if a measured segment is off by a hair it wraps
            // instead of clipping.
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = template.TextColor,
            Margin = new Thickness(0),
        };
        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += OnLyricLineTapped;
        row.GestureRecognizers.Add(tap);
        return row;
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
            if (children[i] is VerticalStackLayout block && block.BindingContext is TimedLyric lyric && lyric.IsActive)
                return i;
        }
        return -1;
    }

    private TimedLyric? FindActiveLyric()
    {
        foreach (var child in LyricsItemsHost.Children)
            if (child is VerticalStackLayout block && block.BindingContext is TimedLyric lyric && lyric.IsActive)
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
        _splitRetriesLeft = 20; // fresh content - fresh measurement budget
        // A centering pass belonging to the previous track must not move the
        // list of the new one.
        _centerFeedbackId++;
        System.Diagnostics.Debug.WriteLine("[LyricsView] auto-follow ON (track changed)");
        // Deferred + coalesced: no synchronous full styling pass on the UI
        // thread while the track switch is still settling.
        ScheduleDistanceStyles(clearCache: true);
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

    /// <summary>
    /// True when the bound lyric collection is known and empty - i.e. there is
    /// nothing the start-scroll polling could ever scroll to.
    /// </summary>
    private bool HasNoLyricItems()
    {
        if (BindableLayout.GetItemsSource(LyricsItemsHost) is System.Collections.ICollection items)
            return items.Count == 0;

        return LyricsItemsHost.Children.Count == 0;
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

        // Nothing to scroll to: the lyric list is empty (a track without timed
        // lyrics, or the queue ended). Polling for ~2s against an empty list
        // only burned layout passes on an invisible control.
        if (HasNoLyricItems())
        {
            _pendingStartScroll = false;
            return false;
        }

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

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        base.OnHandlerChanging(args);

        // The ViewModel (and its ActiveTimedLyrics collection) outlives this
        // view when the pane is torn down. Drop the subscription so a surviving
        // collection does not keep an unloaded view alive, and re-hook it if the
        // view is reattached later.
        if (args.NewHandler == null && _hookedCollection != null)
        {
            _hookedCollection.CollectionChanged -= OnLyricsCollectionChanged;
            _hookedCollection = null;
        }
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
    // The view must reach the display's bottom edge to be drawn UNDER the
    // navigation bar; anything this far short means the bars are reserved
    // outside the window and there is nothing to subtract.
    private const double EdgeToEdgeToleranceDp = 2;

    public static double GetBottomInsetDp(VisualElement? root)
    {
#if ANDROID
        try
        {
            if (root?.Handler?.PlatformView is not global::Android.Views.View rootView)
                return 0;

            var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
            if (decor is null)
                return 0;

            double density = rootView.Resources?.DisplayMetrics?.Density ?? 0;
            if (density <= 0)
                return 0;

            // REAL display size, system bars included. The nav-bar area is only
            // covered by our own content when the view's bottom edge reaches the
            // display's bottom edge. Comparing EDGES (not heights) is what makes
            // this correct on devices with a display cutout, where the page is
            // shorter than the screen at the TOP yet still extends under the
            // bottom bar. (The old height comparison bailed out there and the
            // lyrics were centered ~a nav bar too low.)
            int screenBottomPx = GetDisplayHeightPx(decor);
            if (screenBottomPx <= 0)
                return 0;

            int[] loc = new int[2];
            rootView.GetLocationOnScreen(loc);
            int viewBottomPx = loc[1] + rootView.Height;

            if (viewBottomPx < screenBottomPx - (int)(EdgeToEdgeToleranceDp * density))
                return 0; // bars are reserved outside the window - nothing to subtract

            var insets = decor.RootWindowInsets;
            if (insets is null)
                return 0;

            int bottomPx = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                // GetInsets (NOT GetInsetsIgnoringVisibility): it reports 0
                // while the bars are actually hidden, which is exactly right in
                // immersive mode - the window extends under the bar area, but no
                // bar overlays it, so nothing may be subtracted.
                var bars = insets.GetInsets(global::Android.Views.WindowInsets.Type.SystemBars());
                if (bars != null)
                    bottomPx = bars.Bottom;
            }
            else
            {
                // API 21-29: WindowInsetsCompat-style querying is not available
                // here, but the edge check above already handled immersive mode.
                bottomPx = insets.SystemWindowInsetBottom;
            }

            return bottomPx <= 0 ? 0 : bottomPx / density;
        }
        catch
        {
            return 0;
        }
#else
        return 0;
#endif
    }

#if ANDROID
    /// <summary>
    /// Height of the real display in pixels, system bars included.
    /// </summary>
    private static int GetDisplayHeightPx(global::Android.Views.View decor)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // CurrentWindowMetrics: the current (non-deprecated) way to ask for
            // the window/display bounds.
            var windowManager = decor.Context?.GetSystemService(global::Android.Content.Context.WindowService)
                as global::Android.Views.IWindowManager;
            var bounds = windowManager?.CurrentWindowMetrics?.Bounds;
            if (bounds != null && bounds.Height() > 0)
                return bounds.Height();
        }

        var realMetrics = new global::Android.Util.DisplayMetrics();
#pragma warning disable CA1422 // GetRealMetrics is the only option below API 30
        decor.Display?.GetRealMetrics(realMetrics);
#pragma warning restore CA1422
        return realMetrics.HeightPixels;
    }
#endif
}
