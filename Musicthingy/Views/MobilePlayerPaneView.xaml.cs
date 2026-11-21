using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Musicthingy.Models;
using Musicthingy.Services;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Musicthingy.Helpers;

namespace Musicthingy.Views;

public partial class MobilePlayerPaneView : ContentView
{
    private enum PlayerState { Mini, Medium, FullScreen }
    private enum GestureIntent { None, Resizing, TrackSwipe, ClosingDown }

    private PlayerState _targetState = PlayerState.Mini;
    private GestureIntent _currentGesture = GestureIntent.None;
    private double _startPanHeight;

    // Flag to pause layout-heavy actions (like ScrollTo) during 60fps animations
    private bool _isAnimating;

    // Increments on every animation start; the stuck-animation watchdog below
    // uses it to detect that ITS animation is still the current one.
    private int _animationToken;

    private const double MiniHeight = 72;
    private const double MediumHeight = 296;

    // ====================== Safe-area model (v5) ======================
    // The window is edge-to-edge (MobileMainPage's ContentPage + root Grid are
    // SafeAreaEdges="None"), and - NEW in round 6 - EVERY Layout inside this
    // pane is SafeAreaEdges="None" too: in .NET 10 MAUI, Grid/StackLayout
    // default to Container, so the pane's inner Layouts were auto-padding
    // the strips ON TOP of the code-behind's own padding (a double ~48dp
    // lift that pushed "Now Playing" down and sheared the pills - the
    // round-6 screenshots). Only the pane's CODE owns strip clearance now:
    //
    //  MARGIN + PADDING HANDOFF (no extruding card block):
    //  - Mini & Medium: PlayerRoot.Margin.Bottom = EffectiveSafeBottom - the
    //    card sits ABOVE the gesture/nav strip like a normal mini player, and
    //    the mini progress bar at the pane's bottom edge IS the card's clean
    //    bottom edge. The pane height itself stays the pure content height
    //    (72 / 296), so nothing ever paints over the nav strip.
    //  - FullScreen: the margin eases to 0 while PaneContent.Padding.Bottom
    //    ramps up to EffectiveSafeBottom - the controls stay at a CONSTANT
    //    screen height through the handoff (margin shrink = pad grow) and the
    //    sheet paints edge-to-edge only once it truly IS full-screen.
    //  - Top: PaneContent top padding ramps in over the last 25% of the
    //    Medium->Full stretch, exactly as before (clears the status strip).
    //
    // "Effective" insets self-heal the degenerate case where Shell constrains
    // the page back into the safe box: then the insets are already consumed
    // by the host and the pane adds none of its own.
    private double SafeTop => SafeAreaService.TopInset;
    private double SafeBottom => SafeAreaService.BottomInset;

    // Real window height in dp, from the display metrics (independent of
    // whatever box the pane happens to be arranged in).
    private double WindowHeightDp
    {
        get
        {
            var info = DeviceDisplay.MainDisplayInfo;
            if (info.Height > 0 && info.Density > 0)
                return info.Height / info.Density;
            return FullScreenHeight;
        }
    }

    // True when the pane's host really is edge-to-edge: the full-screen pane
    // height must run past the strip-free window height by roughly the inset
    // sum. If Shell constrains the page to the safe box instead, FullScreenHeight
    // falls short of the window height and the insets are already consumed -
    // adding them again would overflow both ends (the round-5 screenshots).
    private bool IsEdgeToEdge
    {
        get
        {
            double insetSum = SafeTop + SafeBottom;
            return insetSum > 0 && FullScreenHeight >= WindowHeightDp - (insetSum * 0.4);
        }
    }

    private double EffectiveSafeTop => IsEdgeToEdge ? SafeTop : 0;
    private double EffectiveSafeBottom => IsEdgeToEdge ? SafeBottom : 0;

    // Height the expanded zone is pushed down by the header when fully open
    // (lyrics mode: the compact header stays on top). The zone is always laid
    // out at (screen - shift) and clipped by the pane, so its content stays
    // put while the pane grows.
    private const double ExpandedHeaderShift = 102;

    // Same, in artwork mode once the FullScreen "Now Playing" header replaces
    // the compact one (it is shorter: 60, not 102).
    private const double ExpandedHeaderShiftFull = 60;

    // Vertical space the expanded zone spends outside the media slot:
    // Padding="16,10,16,16" (26 vertical), the 12px stack gap between the
    // media slot and ExpandedControls, and the tags row's negative top margin
    // (-10). TagsRow now lives INSIDE ExpandedControls (round 6), so its
    // height is part of ExpandedControls' own measure, not the chrome.
    private const double ExpandedZoneFixedChrome = 26 + 12 - 10;

    // Fallbacks for the slot math before the first measure pass.
    // ExpandedControls now also carries the tags row (28) above the slider.
    private const double FallbackControlsHeight = 230;
    private const double FallbackInfoHeight = 56;

    // Artwork / lyrics toggle inside the full player's media slot.
    private bool _isLyricsMode;

    // Last safe-area padding applied to PaneContent (change guard only;
    // see UpdateSafeAreaPadding).
    private Thickness _safeAreaPadding;

    // Last margin applied to PlayerRoot (change guard only; see
    // UpdateSafeAreaPadding - the mini/medium bottom clearance).
    private Thickness _playerRootMargin;

    public System.Windows.Input.ICommand OpenTagsCommand { get; }

    private readonly PanVelocityTracker _velocityTracker = new();

    public MobilePlayerPaneView()
    {
        InitializeComponent();
        SetupGestures();

        // First layout pass at the pure mini content height (the gesture
        // clearance lives in PlayerRoot.Margin, not the pane height).
        PlayerRoot.HeightRequest = MiniHeight;
        UpdateLayoutForHeight(MiniHeight);
        ApplyMediaMode();

        OpenTagsCommand = new Command<PlaylistItem>(OnOpenTagsClicked);

        // Tapping a timed lyric line seeks the player to that timestamp
        LyricsZone.LyricTapped += OnLyricTapped;

        // Safe-area insets are unreadable before the first layout pass; the
        // pane-level SizeChanged fires right after it, before the user can
        // open anything, so applying there is enough (and it also covers
        // rotation: any later inset change is picked up by the same handler).
        // Mini self-heal: if the pane is resting at mini while the inset
        // values settle, re-apply the state height + margin so the card sits
        // exactly 72dp tall above the gesture strip (see UpdateSafeAreaPadding).
        //
        // ROUND 9 - rotation hard reset: a WIDTH change means the device (or
        // window) rotated. Rotation is the one input the pane's incremental
        // machinery cannot absorb: the PlayerResize Commit is mid-flight
        // writing old-geometry heights, pan gesture state was captured
        // against the old screen, the swipe carousel's pages sit at the old
        // width, and the close-drag's TranslationY survives with no writer
        // left to clear it (the user's report: title slowly drifting off
        // screen after rotating, never returning, pane broken until app
        // restart - the dead animation/marquee callbacks kept writing
        // translations into a geometry they were built for). Instead of
        // letting all that run to completion, a width change aborts
        // everything and rebuilds the pane's state ABSOLUTELY from the
        // current target state. Every one of these writes is idempotent, so
        // the reset is also safe to run more than once.
        double lastKnownWidth = -1;
        this.SizeChanged += (_, __) =>
        {
            double currentWidth = this.Width;
            bool rotated = lastKnownWidth > 0 && Math.Abs(currentWidth - lastKnownWidth) > 1;

            if (rotated)
                RebuildForRotation();
            lastKnownWidth = currentWidth;

            if (_targetState == PlayerState.Mini && _currentGesture == GestureIntent.None && !_isAnimating)
            {
                PlayerRoot.HeightRequest = MiniHeight;
                UpdateLayoutForHeight(MiniHeight);
            }
            else
            {
                double h = PlayerRoot.Height > 0 ? PlayerRoot.Height : PlayerRoot.HeightRequest;
                UpdateSafeAreaPadding(h,
                    FullScreenHeight > MediumHeight
                        ? Math.Clamp((h - MediumHeight) / (FullScreenHeight - MediumHeight), 0, 1)
                        : 0);
            }
            UpdateArtworkSize(GetFullScreenSlotHeight());
        };

        // The media slot is sized manually (see UpdateLayoutForHeight); once it
        // is measured, fit the square artwork to the smaller of its width/height.
        // The artwork itself is sized ONCE per slot width change - never per
        // animation frame - and the open/close transition only fades/scales it.
        MediaSlot.SizeChanged += (_, __) => UpdateArtworkSize(GetFullScreenSlotHeight());
        this.SizeChanged += (_, __) => UpdateArtworkSize(GetFullScreenSlotHeight());
        ExpandedControls.SizeChanged += (_, __) => UpdateLayoutForHeight(
            PlayerRoot.Height > 0 ? PlayerRoot.Height : PlayerRoot.HeightRequest);

        // Bottom progress bar: keep the fill width in sync with playback.
        // Re-measure when the bar gets its real width, and re-hook whenever a
        // new BindingContext (ViewModel) arrives.
        BottomProgressBar.SizeChanged += (_, __) => UpdateBottomProgress();
        this.BindingContextChanged += (_, __) => HookBottomProgressVm();
        HookBottomProgressVm(); // no-op if BindingContext is not assigned yet

        // Track swipe carousel: keep the next/previous preview pages in sync
        // with the queue whenever the binding context (or its queue) changes.
        this.BindingContextChanged += (_, __) => HookSwipeVm();
        HookSwipeVm(); // no-op if BindingContext is not assigned yet

        WeakReferenceMessenger.Default.Register<ActiveLyricChangedMessage>(this, (r, m) =>
        {
            if (_isAnimating || _currentGesture != GestureIntent.None)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_targetState == PlayerState.FullScreen && _isLyricsMode && m.Lyric != null)
                {
                    // allowResume: this call IS a lyric switch, so paused
                    // lyric-following may resume (LyricsView still checks its
                    // own idle/middle-zone rules)
                    LyricsZone.ScrollToLyric(m.Lyric, animate: true, allowResume: true);
                }
            });
        });
    }

    private double FullScreenHeight
    {
        get
        {
            var parent = this.Parent as VisualElement;
            if (parent != null && parent.Height > 0)
                return parent.Height;
            var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            return activePage?.Height ?? 800;
        }
    }

    /// <summary>
    /// Safe-area clearance for the pane - the margin/padding HANDOFF model
    /// (v5). Two coordinated writes, both smooth functions of
    /// (height, insets), no boolean flips:
    ///
    /// 1. PlayerRoot.Margin.Bottom = EffectiveSafeBottom * (1 - progressToFull):
    ///    while the pane is mini/medium the card floats ABOVE the gesture/nav
    ///    strip like a normal mini player - nothing paints over the strip and
    ///    the mini progress bar at the pane's bottom edge is the card's clean
    ///    bottom (no CardColor slab extruding below the bar - the round-6
    ///    screenshot). At full screen the margin eases to exactly 0.
    ///
    /// 2. PaneContent.Padding:
    ///    - Bottom = EffectiveSafeBottom * progressToFull: grows exactly as
    ///      the margin shrinks, so the bottom content (transport, pills) stays
    ///      at a CONSTANT screen height through the whole handoff and rests
    ///      SafeBottom above the true screen bottom once the sheet is
    ///      full-bleed.
    ///    - Top = EffectiveSafeTop ramped over the last 25% of the
    ///      Medium->Full stretch (clears the status strip exactly when the
    ///      pane top reaches it; the old boolean flip produced the
    ///      final-frame snap + jitter).
    ///
    /// The Effective* indirection zeroes everything if the host is NOT
    /// actually edge-to-edge (Shell constraining the page), so the pane never
    /// double-pads clearance it already gets.
    /// </summary>
    private void UpdateSafeAreaPadding(double height, double progressToFull)
    {
        // 1. Margin handoff: mini/medium sit above the strip, full is flush.
        double bottomMargin = EffectiveSafeBottom * (1.0 - progressToFull);
        var targetMargin = new Thickness(0, 0, 0, bottomMargin);
        if (_playerRootMargin != targetMargin)
        {
            _playerRootMargin = targetMargin;
            PlayerRoot.Margin = targetMargin;
        }

        // 2. Content padding: the strip clearance that REPLACES the margin.
        double topPad = EffectiveSafeTop * Math.Clamp((progressToFull - 0.75) / 0.25, 0, 1);
        double bottomPad = EffectiveSafeBottom * progressToFull;

        Thickness targetPad = new Thickness(0, topPad, 0, bottomPad);
        if (_safeAreaPadding != targetPad)
        {
            _safeAreaPadding = targetPad;
            PaneContent.Padding = targetPad;
        }
    }

    private void SetupGestures()
    {
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;

        // Single tap recognizer for the whole header. Android only reports a
        // TAP here when the finger goes down and up without exceeding touch
        // slop - a real swipe is routed to the pan recognizer above instead,
        // so clicks and swipes never fight each other (native behavior).
        // Nothing inside the header owns a TapGestureRecognizer; child ones
        // would consume the touch stream and starve these pan gestures.
        var tap = new TapGestureRecognizer();
        tap.Tapped += OnHeaderTapped;

        HeaderDragZone.GestureRecognizers.Clear();
        HeaderDragZone.GestureRecognizers.Add(pan);
        HeaderDragZone.GestureRecognizers.Add(tap);

        // Round 7: the big artwork card is ALSO a drag surface (the user asked
        // to drag the pane closed from the big icon, not just the top strip).
        // Same recognizers, same handlers: OnPanUpdated resolves the gesture
        // by direction (down = collapse/close, up = expand) exactly as the
        // header does, because it reads the PANE's height, not where the drag
        // started. No tap recognizer here - a tap on the artwork does nothing
        // (consumed by no one), and a genuine swipe still reaches this pan
        // recognizer because the Image inside is not tap-consumable either.
        var artPan = new PanGestureRecognizer();
        artPan.PanUpdated += OnPanUpdated;
        ArtworkCard.GestureRecognizers.Clear();
        ArtworkCard.GestureRecognizers.Add(artPan);
        _artworkPanRecognizer = artPan;
    }

    // The artwork card's own pan recognizer (SetupGestures): identifying the
    // sender lets OnPanUpdated escalate a body-originated DOWN drag to a
    // closing translation once the height is clamped at Mini (see the
    // Resizing branch). Header-originated drags keep their old snap-only
    // behavior, so nothing changes for existing muscle memory.
    private PanGestureRecognizer? _artworkPanRecognizer;

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // Kill the snap animation BEFORE the finger takes over: without
                // this, a still-running "PlayerResize" Commit and the pan both
                // write PlayerRoot.HeightRequest every frame and visibly fight.
                this.AbortAnimation("PlayerResize");
                _startPanHeight = PlayerRoot.Height > 0 ? PlayerRoot.Height : PlayerRoot.HeightRequest;
                _currentGesture = GestureIntent.None;

                _velocityTracker.Start();
                PrepareVisibilityForAnimation();
                break;

            case GestureStatus.Running:
                _velocityTracker.Update(e.TotalX, e.TotalY);

                if (_currentGesture == GestureIntent.None)
                {
                    if (Math.Abs(e.TotalX) > 15 || Math.Abs(e.TotalY) > 15)
                    {
                        // Mini means the pane rests at the pure 72dp content
                        // height (the strip clearance is the margin, not the
                        // height); the +1 guards float rounding.
                        if (_startPanHeight <= MiniHeight + 1)
                        {
                            // MINI STATE: clear horizontal = track swipe carousel
                            // (checked FIRST so mostly-horizontal diagonals swipe);
                            // vertical = expand / close (unchanged behaviors)
                            if (Math.Abs(e.TotalX) > 12 && Math.Abs(e.TotalX) > Math.Abs(e.TotalY) * 1.2 && !_swipeSettling)
                                _currentGesture = GestureIntent.TrackSwipe;
                            else if (e.TotalY < -10 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX) * 0.5)
                                _currentGesture = GestureIntent.Resizing;
                            else if (e.TotalY > 20 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX) * 0.5)
                                _currentGesture = GestureIntent.ClosingDown;
                        }
                        else
                        {
                            _currentGesture = GestureIntent.Resizing;
                        }
                    }
                }

                if (_currentGesture == GestureIntent.TrackSwipe)
                {
                    _swipeTotalX = e.TotalX;
                    UpdateTrackSwipe(e.TotalX);
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    if (e.TotalY > 0) PlayerRoot.TranslationY = e.TotalY;
                }
                else if (_currentGesture == GestureIntent.Resizing)
                {
                    double newHeight = _startPanHeight - e.TotalY;
                    newHeight = Math.Clamp(newHeight, MiniHeight, FullScreenHeight);

                    // Round 7: a body-originated drag (the big artwork card is
                    // a pan surface now) that runs DOWN past the mini clamp
                    // escalates to the same closing translation the mini
                    // header's drag-to-close uses, so dragging from the big
                    // artwork can close the pane outright, not just collapse
                    // it to the mini bar. Translation starts only AFTER the
                    // height hits the clamp (slack consumed first), so the
                    // sheet edge follows the finger with no jump. Header
                    // drags keep the old snap-only behavior (sender differs).
                    double collapseSlack = _startPanHeight - MiniHeight;
                    if (newHeight <= MiniHeight + 1 && e.TotalY > collapseSlack + 1 && ReferenceEquals(sender, _artworkPanRecognizer))
                    {
                        _currentGesture = GestureIntent.ClosingDown;
                        PlayerRoot.TranslationY = e.TotalY - collapseSlack;
                    }
                    else
                    {
                        PlayerRoot.HeightRequest = newHeight;
                        UpdateLayoutForHeight(newHeight);
                    }
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_currentGesture == GestureIntent.TrackSwipe)
                {
                    FinishTrackSwipe();
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    // Require at least 40px of panning to count as a flick close
                    if (PlayerRoot.TranslationY > 50 || (_velocityTracker.VelocityY > 1.0 && PlayerRoot.TranslationY > 40)) ClosePlayer(_velocityTracker.VelocityY);
                    else
                    {
                        uint duration = 250;
                        if (_velocityTracker.VelocityY < -0.1) duration = (uint)Math.Clamp(Math.Abs(PlayerRoot.TranslationY / _velocityTracker.VelocityY), 100, 350);
                        PlayerRoot.TranslateToAsync(0, 0, duration, Easing.CubicOut);
                        SnapToBestState(_velocityTracker.VelocityY, 0);
                    }
                }
                else
                {
                    // Pass the total distance the height was altered to verify it was a real drag
                    SnapToBestState(_velocityTracker.VelocityY, Math.Abs(PlayerRoot.Height - _startPanHeight));
                }

                _currentGesture = GestureIntent.None;
                break;
        }
    }

    private void SnapToBestState(double velocityY = 0, double totalMoved = 0)
    {
        double currentHeight = PlayerRoot.Height;

        // A flick requires a deliberate speed AND at least 40 pixels of movement
        bool isFlick = Math.Abs(velocityY) > 1.0 && totalMoved > 40;

        // Flick UP (expanding)
        if (isFlick && velocityY < 0)
        {
            if (currentHeight > MediumHeight - 20) AnimateToState(PlayerState.FullScreen, velocityY);
            else AnimateToState(PlayerState.Medium, velocityY);
            return;
        }

        // Flick DOWN (shrinking)
        if (isFlick && velocityY > 0)
        {
            if (currentHeight > MediumHeight + 20) AnimateToState(PlayerState.Medium, velocityY);
            else AnimateToState(PlayerState.Mini, velocityY);
            return;
        }

        // Positional fallback (if moved slowly or barely twitched)
        if (currentHeight > (FullScreenHeight * 0.6)) AnimateToState(PlayerState.FullScreen, velocityY);
        else if (currentHeight > (MiniHeight + 80)) AnimateToState(PlayerState.Medium, velocityY);
        else AnimateToState(PlayerState.Mini, velocityY);
    }

    private void AnimateToState(PlayerState state, double velocityY = 0)
    {
        // Same rule as the pan start: never let two writers animate the height.
        this.AbortAnimation("PlayerResize");
        _targetState = state;
        PrepareVisibilityForAnimation();

        double targetHeight = state switch
        {
            PlayerState.FullScreen => FullScreenHeight,
            PlayerState.Medium => MediumHeight,
            _ => MiniHeight
        };

        uint duration = 250;
        double distance = Math.Abs(targetHeight - PlayerRoot.Height);

        if (Math.Abs(velocityY) > 0.1)
        {
            duration = (uint)Math.Clamp(distance / Math.Abs(velocityY), 100, 350);
        }

        Animation heightAnimation = new Animation(
            callback: h =>
            {
                PlayerRoot.HeightRequest = h;
                UpdateLayoutForHeight(h);
            },
            start: PlayerRoot.Height,
            end: targetHeight,
            easing: Easing.CubicOut);

        heightAnimation.Commit(this, "PlayerResize", length: duration, finished: (v, c) =>
        {
            FinalizeVisibilityForState();
        });
    }

    private async void ClosePlayer(double velocity = 0)
    {
        _isAnimating = true;
        uint length = 200;

        // Height + current bottom margin + 60 => the card clears the screen
        // edge (the margin sits between the pane and the window bottom).
        double offScreen = MiniHeight + _playerRootMargin.Bottom + 60;
        if (velocity > 0.1) length = (uint)Math.Clamp((offScreen - PlayerRoot.TranslationY) / velocity, 100, 350);
        await PlayerRoot.TranslateToAsync(0, offScreen, length, Easing.CubicOut);

        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm)
        {
            if (vm.CurrentTrack != null) vm.CurrentTrack.IsSelected = false;
            vm.CurrentTrack = null;
            vm.PlaybackQueue?.Clear();
        }

        PlayerRoot.TranslationY = 0;
        PlayerRoot.HeightRequest = MiniHeight;
        _targetState = PlayerState.Mini;

        UpdateLayoutForHeight(MiniHeight);
        FinalizeVisibilityForState();
    }

    private void PrepareVisibilityForAnimation()
    {
        // Kill any in-flight swipe carousel settle so the header always
        // expands/collapses from its resting position.
        ResetSwipeGeometry();

        _isAnimating = true;
        MiniControls.IsVisible = true;
        ExpandedHeaderControls.IsVisible = true;
        HeaderContentGrid.IsVisible = true;
        FullScreenHeader.IsVisible = !_isLyricsMode;
        DragHandle.IsVisible = true;
        ExpandedContentZone.IsVisible = true;

        // Lyrics-mode prep only when the lyrics view is (about to be) shown:
        // its layout pass is heavy and pointless while the artwork is the mode.
        if (_isLyricsMode)
            LyricsZone.PrepareForAnimation();

        // Watchdog: ONLY the animations' finished callbacks clear _isAnimating,
        // and an interrupted MAUI animation can leave its finished callback
        // (and its await) permanently pending. While _isAnimating is stuck,
        // every ActiveLyricChangedMessage is dropped in the handler below and
        // the highlighted line + lyric following drift out of sync with playback.
        // If the flag is still set well after the longest possible animation
        // (~350ms) - and no drag is in progress - force-finalize so lyric
        // following always recovers.
        int token = ++_animationToken;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(700), () =>
        {
            if (token != _animationToken || !_isAnimating)
                return false; // superseded by a newer animation, or finished normally
            if (_currentGesture != GestureIntent.None)
                return true; // user is still dragging - keep waiting
            FinalizeVisibilityForState();
            return false;
        });
    }

    private void FinalizeVisibilityForState()
    {
        _isAnimating = false;

        // Only safely update IsVisible when the animation has completely stopped
        MiniControls.IsVisible = MiniControls.Opacity > 0.01;
        ExpandedHeaderControls.IsVisible = ExpandedHeaderControls.Opacity > 0.01;
        HeaderContentGrid.IsVisible = HeaderContentGrid.Opacity > 0.01;
        HeaderContentGrid.InputTransparent = HeaderContentGrid.Opacity < 0.5;
        FullScreenHeader.IsVisible = !_isLyricsMode && FullScreenHeader.Opacity > 0.01;
        FullScreenHeader.InputTransparent = FullScreenHeader.Opacity < 0.5;
        DragHandle.IsVisible = DragHandle.Opacity > 0.01;
        ExpandedContentZone.IsVisible = PlayerRoot.HeightRequest > (MiniHeight + 5);
        if (_isLyricsMode)
            LyricsZone.FinalizeVisibility();

        // Re-sync lyrics to current position now that the animation has safely
        // completed (only when the lyric view is actually the visible mode).
        if (_targetState == PlayerState.FullScreen && _isLyricsMode)
        {
            LyricsZone.ScrollToActiveLyric(animate: true);
        }
    }

    private void UpdateLayoutForHeight(double height)
    {
        double progressToMedium = Math.Clamp((height - MiniHeight) / (MediumHeight - MiniHeight), 0, 1);

        double fullHeight = FullScreenHeight;
        double progressToFull = 0;
        if (fullHeight > MediumHeight)
        {
            progressToFull = Math.Clamp((height - MediumHeight) / (fullHeight - MediumHeight), 0, 1);
        }

        // Margin/padding handoff (v5): mini/medium float above the gesture
        // strip via PlayerRoot.Margin; full screen eases the margin to 0
        // while PaneContent's bottom padding takes over - bottom content
        // stays at a constant screen height through the whole transition.
        UpdateSafeAreaPadding(height, progressToFull);

        // Inner height = what the pane has INSIDE PaneContent's safe-area
        // padding. Everything below sizes against THIS value, so the zone,
        // slot and controls can never overhang the pane's padded bottom
        // (the round-5 "pills sliced in half below y=2280" bug was the zone
        // using the FULL pane height while the content sat inside the pad).
        //
        // The zone is anchored (VerticalOptions=Start) to the TOP of the
        // pane and pushed down by the header shift (TranslationY further
        // below), so its height must be the INNER height MINUS that shift -
        // its bottom then lands exactly on the padded content's bottom edge
        // when fully expanded. Uses the FULL-EXPANSION header height, not
        // the animated one, so the zone's height stays constant during the
        // Mini->Medium transition instead of forcing a full relayout every
        // animation frame.
        double fullInnerHeight = Math.Max(0, fullHeight - EffectiveSafeTop - EffectiveSafeBottom);
        double zoneHeight = Math.Max(0, fullInnerHeight - (_isLyricsMode ? ExpandedHeaderShift : ExpandedHeaderShiftFull));
        if (ExpandedContentZone.HeightRequest < 0 || Math.Abs(ExpandedContentZone.HeightRequest - zoneHeight) > 1)
        {
            ExpandedContentZone.HeightRequest = zoneHeight;
        }

        // PERF: everything below is a pure GPU transform / opacity write.
        // No WidthRequest / HeightRequest / Margin mutation happens per frame:
        // each of those forces a full Measure+Arrange pass over this subtree
        // (including the RoundRectangle clip re-path of the border), which was
        // the source of the 60fps drag stutter. The header's medium-state
        // growth (thumbnail 44 -> 66) is expressed as Scale + TranslationX/Y
        // on the fixed mini header instead of per-frame Width/Height/Margin
        // writes: same visual result, zero relayouts per frame.
        TrackImageBorder.Scale = 1.0 + (0.50 * progressToMedium);
        HeaderTitleStack.TranslationX = 22 * progressToMedium;
        HeaderContentGrid.TranslationY = 12 * progressToMedium;

        MiniControls.Opacity = Math.Clamp(1.0 - (progressToMedium / 0.3), 0, 1);

        // Fade IN the overall Expanded controls container during Mini->Medium
        double expandedOpacity = Math.Clamp((progressToMedium - 0.3) / 0.7, 0, 1);
        ExpandedHeaderControls.Opacity = expandedOpacity;
        ExpandedContentZone.Opacity = expandedOpacity;

        // Bottom progress bar is a MINI-state-only element: the expanded
        // states have the real position slider instead. At mini the pane
        // floats above the gesture strip via the margin, so the bar flush
        // at the pane's bottom edge is the card's clean bottom edge.
        BottomProgressBar.IsVisible = progressToMedium <= 0;

        // Cross-fade the compact header out and the OpenTune "Now Playing"
        // header in during the Medium->Full stretch (artwork mode only).
        // In lyrics mode the compact header stays as the anchor.
        if (!_isLyricsMode)
        {
            double compactFadeOut = 1.0 - Math.Clamp(progressToFull / 0.45, 0, 1);
            double fullFadeIn = Math.Clamp((progressToFull - 0.45) / 0.55, 0, 1);
            HeaderContentGrid.Opacity = compactFadeOut;
            HeaderContentGrid.InputTransparent = compactFadeOut < 0.5;
            FullScreenHeader.Opacity = fullFadeIn;
            DragHandle.Opacity = expandedOpacity * compactFadeOut;
        }
        else
        {
            HeaderContentGrid.Opacity = 1;
            HeaderContentGrid.InputTransparent = false;
            FullScreenHeader.Opacity = 0;
            DragHandle.Opacity = expandedOpacity;
        }

        // Flatten the outer sheet chrome once (at the last 2% of the stretch):
        // FullScreen is edge-to-edge, so the 1px stroke + 16dp top corners only
        // make sense while the sheet still looks like a sheet. Guarded by the
        // current values so an unchanged frame writes nothing.
        bool isFull = progressToFull >= 0.98;
        double targetStroke = isFull ? 0 : 1;
        if (Math.Abs(PlayerRoot.StrokeThickness - targetStroke) > 0.1)
        {
            PlayerRoot.StrokeThickness = targetStroke;
            PlayerRootShape.CornerRadius = isFull ? new CornerRadius(0) : new CornerRadius(16, 16, 0, 0);
        }

        // Header zone shift: 72 -> 102 through Mini->Medium (the grown compact
        // header), then back to 60 as the shorter Now Playing header replaces
        // it in artwork mode. The full-screen header itself is pushed below the
        // status-bar strip by PaneContent's top padding (UpdateSafeAreaPadding),
        // so the compact header's shift value stays in the sheet's coordinate
        // space.
        double headerShift = _isLyricsMode
            ? 72 + (30 * progressToMedium)
            : 72 + (30 * progressToMedium) - (42 * progressToFull);
        ExpandedContentZone.TranslationY = headerShift;

        // ROUND 9 stutter fix: UpdateForFullScreenProgress used to run on
        // EVERY frame of the resize while lyrics mode was on - even while the
        // pane was still climbing Mini->Medium (progressToFull == 0 the whole
        // time, where it had nothing to do), and even when the track had NO
        // lyrics at all (the "no lyrics" prompt is the only thing that path
        // touches then). Its UpdateSpacers walks the visual tree to the root
        // AND queries platform insets each call - at 60fps that is the
        // perceivable stutter while opening/closing the player in lyrics
        // mode. Gated to the stretch where it has work (progressToFull > 0),
        // the pane's own per-frame writes above are pure transforms/opacity.
        if (_isLyricsMode && fullHeight > MediumHeight && progressToFull > 0)
            LyricsZone.UpdateForFullScreenProgress(progressToFull);

        // Bottom actions belong to the full player; fade them in only near the
        // top so no sliver of a pill peeks out of the medium (clipped) zone.
        double pillsOpacity = Math.Clamp((progressToFull - 0.65) / 0.35, 0, 1);
        BottomActions.Opacity = pillsOpacity;
        BottomActions.InputTransparent = pillsOpacity < 0.5;

        // Slot sizing uses the pane's INNER height (inside the safe-area
        // padding), so the controls stay fully inside the padded bottom.
        double innerHeight = Math.Max(0, height - _safeAreaPadding.Top - _safeAreaPadding.Bottom);
        UpdateMediaSlotForHeight(innerHeight, headerShift);
    }

    /// <summary>
    /// Height the media slot has at a fully expanded pane - the reference for
    /// sizing the artwork once and for scaling it during the transition.
    /// Computed from the pane's INNER height (inside PaneContent's safe-area
    /// padding) minus the header shift, so the artwork, title row and
    /// controls together exactly fill the padded full-screen sheet.
    /// </summary>
    private double GetFullScreenSlotHeight()
    {
        double shift = _isLyricsMode ? ExpandedHeaderShift : ExpandedHeaderShiftFull;
        double fullInnerHeight = Math.Max(0, FullScreenHeight - EffectiveSafeTop - EffectiveSafeBottom);
        double visibleZoneHeight = Math.Max(0, fullInnerHeight - shift);
        // TagsRow lives INSIDE ExpandedControls (round 6), so the controls'
        // measured height already carries it - no separate tags term.
        double controlsHeight = ExpandedControls.Height > 1 ? ExpandedControls.Height : FallbackControlsHeight;
        return Math.Max(0, visibleZoneHeight - ExpandedZoneFixedChrome - controlsHeight);
    }

    /// <summary>
    /// Sizes the media slot (artwork / lyrics) from the part of the expanded
    /// zone that is actually visible at the CURRENT pane height - the INNER
    /// height (inside the safe-area padding) passed by the caller, never the
    /// stale PlayerRoot.Height (which reflects the PREVIOUS frame's measure
    /// and made the slot trail one frame behind the sheet edge).
    /// At medium the slot collapses to zero and the controls sit directly
    /// under the tags; as the pane opens, the slot absorbs every freed pixel,
    /// pushing the controls down until they rest on the padded content's
    /// bottom edge at full screen.
    /// </summary>
    private void UpdateMediaSlotForHeight(double innerHeight, double headerShift)
    {
        double visibleZoneHeight = Math.Max(0, innerHeight - headerShift);
        // TagsRow is part of ExpandedControls' own height now (round 6).
        double controlsHeight = ExpandedControls.Height > 1 ? ExpandedControls.Height : FallbackControlsHeight;

        double slotHeight = Math.Max(0, visibleZoneHeight - ExpandedZoneFixedChrome - controlsHeight);

        // Same whole-pixel guard as the zone: invisible sub-pixel slots flicker.
        if (slotHeight < 1)
            slotHeight = 0;

        if (Math.Abs(MediaSlot.HeightRequest - slotHeight) > 0.5)
            MediaSlot.HeightRequest = slotHeight;

        bool showSlot = slotHeight > 0;
        if (MediaSlot.IsVisible != showSlot)
            MediaSlot.IsVisible = showSlot;

        if (showSlot && !_isLyricsMode)
        {
            // Artwork mode: the card is sized ONCE at the full-screen slot
            // (see UpdateArtworkSize); the transition merely fades/scales
            // it, so no image resampling or re-measure happens per frame.
            double fullSlot = GetFullScreenSlotHeight();
            double fill = fullSlot > 1 ? Math.Clamp(slotHeight / fullSlot, 0, 1) : 1;
            ArtworkPanel.Opacity = Math.Clamp(fill, 0, 1);
            ArtworkPanel.Scale = 0.92 + (0.08 * fill);
        }
    }

    /// <summary>
    /// Sizes the square cover art ONCE for the full-screen slot - it is NOT
    /// resized per frame anymore (that re-sampled the bitmap at 60fps and
    /// re-clipped its rounded border every frame). During the transition the
    /// card only fades/scales via GPU transforms; at rest it fills the slot.
    /// Side = min(slot width - 24 breathing room, full slot height minus the
    /// title/buttons row), so the whole artwork page fits at full screen.
    /// The row below the artwork (title + bare 3-dot options) is Auto-height;
    /// only its ~56dp fallback is subtracted here.
    /// </summary>
    private void UpdateArtworkSize(double slotHeight)
    {
        double width = MediaSlot.Width;
        if (width <= 1)
            width = ExpandedContentZone.Width - 32;
        if (width <= 1)
            width = PlayerRoot.Width - 32;
        if (width <= 1)
            return;

        double side = Math.Min(width - 24, slotHeight - FallbackInfoHeight - 16);
        if (side <= 1)
            return;

        if (Math.Abs(ArtworkCard.WidthRequest - side) > 0.5)
            ArtworkCard.WidthRequest = side;
        if (Math.Abs(ArtworkCard.HeightRequest - side) > 0.5)
            ArtworkCard.HeightRequest = side;

        // The card is visible whenever the artwork page is (it lost a stale
        // IsVisible="False" default in XAML once; this reasserts it cheaply
        // at the same sizing points).
        if (!ArtworkCard.IsVisible)
            ArtworkCard.IsVisible = true;
    }

    // ===================== Full player: artwork / lyrics =====================

    /// <summary>
    /// Lyrics pill: swaps the media slot between the cover art and the lyrics
    /// view. The pill keeps a selected (tonal) look while lyrics are showing.
    /// </summary>
    private async void OnLyricsToggleClicked(object? sender, TappedEventArgs e)
    {
        _isLyricsMode = !_isLyricsMode;

        // Round 8 lag fix: defer the media-slot swap off the TAP frame. The
        // old code ran ApplyMediaMode synchronously inside the tap handler:
        // two IsVisible flips + UpdateLayoutForHeight + FinalizeVisibility
        // ALL executed before the handler returned, blocking the input thread
        // for a full layout cycle of the entire lyric stack (potentially
        // hundreds of wrapped rows) - that is the perceivable hitch when
        // opening/closing the lyrics view. Posting the swap one dispatcher
        // cycle later lets the pill's pressed/selected visual feedback paint
        // FIRST (instant responsiveness), and the heavy swap runs as its own
        // batched layout update instead of piggy-backing the tap event.
        LyricsPill.SetDynamicResource(Border.BackgroundColorProperty, _isLyricsMode ? "SurfaceSelected" : "SurfaceRaised");

        await Task.Delay(1); // leave the tap frame; run on the dispatcher's next pass
        ApplyMediaMode();

        if (!_isLyricsMode)
            return;

        // Let the newly visible lyrics view lay out before centering it.
        await Task.Delay(32);
        LyricsZone.ScrollToActiveLyric(animate: false);
    }

    /// <summary>Queue pill placeholder - the queue UI is the next piece.</summary>
    private void OnQueueClicked(object? sender, TappedEventArgs e)
    {
        // Intentionally empty: the pill exists so the final layout is in place
        // and is wired to the queue surface when that lands.
    }

    /// <summary>
    /// Round 8: the big play button's tap surface. The glyph is a plain Image
    /// now (see the XAML comment for why: deterministic centering), so the
    /// command that used to ride on the ImageButton is executed here instead.
    /// Mirrors TogglePlayCommand: guarded the same way an ImageButton's
    /// Command is (CanExecute).
    /// </summary>
    private void OnPlayPauseTapped(object? sender, TappedEventArgs e)
    {
        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm &&
            vm.TogglePlayCommand?.CanExecute(null) == true)
        {
            vm.TogglePlayCommand.Execute(null);
        }
    }

    private void ApplyMediaMode()
    {
        // Round 8 lag fix (lyrics open/close): the two IsVisible flips each
        // trigger a synchronous full Measure+Arrange of the media-slot subtree
        // - showing the lyric stack (hundreds of wrapped rows) is the
        // expensive one, and running it inside the TAP handler blocked the
        // input thread for a whole layout cycle (the perceivable hitch).
        // OnLyricsToggleClicked now defers the call by one dispatcher cycle,
        // so this runs as its own batched layout update, after the pill's
        // selected-state feedback has already painted.
        LyricsZone.IsVisible = _isLyricsMode;
        ArtworkPanel.IsVisible = !_isLyricsMode;
        if (!_isLyricsMode)
            ArtworkCard.IsVisible = true;

        LyricsPill.SetDynamicResource(Border.BackgroundColorProperty, _isLyricsMode ? "SurfaceSelected" : "SurfaceRaised");

        // Re-evaluate the header + slot for the mode switch at the pane's
        // current height (compact header stays in lyrics mode; Now Playing
        // header only shows in artwork mode).
        double h = PlayerRoot.Height > 0 ? PlayerRoot.Height : PlayerRoot.HeightRequest;
        UpdateLayoutForHeight(h);
        FinalizeVisibilityForState();
    }

    /// <summary>Collapse chevron in the Now Playing header: pull the sheet
    /// back to the medium height (a second tap/collapse goes to Mini).</summary>
    private void OnCollapseChevronTapped(object? sender, TappedEventArgs e)
        => AnimateToState(_targetState == PlayerState.FullScreen ? PlayerState.Medium : PlayerState.Mini);

    /// <summary>Title/artist block under the artwork: same behavior as the
    /// old compact-header title tap - scroll the track list to the current
    /// track, collapsing to medium first when opened full-screen.</summary>
    private void OnFullTitleTapped(object? sender, TappedEventArgs e)
        => HandleTrackNameTap();

    private void OnOpenTagsClicked(PlaylistItem? track)
    {
        if (track == null || this.BindingContext is not Musicthingy.ViewModels.MainViewModel vm)
            return;

        List<string> allTags = vm.GetAllKnownTags();

        var editSheet = new EditTrackSheet(track, allTags, initialTabIndex: 2);

        // Wire up the CloseAction so the Sheet Host knows when to safely slide down,
        // fade out the scrim, and remove the sheet from the visual stack.
        editSheet.CloseAction = async (result) =>
        {
            // Saving may have replaced the lyrics of the track that is
            // playing; re-parse them so the pane updates immediately.
            if (result is true && this.BindingContext is Musicthingy.ViewModels.MainViewModel viewModel)
                viewModel.RefreshLyricsView();

            if (Services.SheetManager.Host != null)
            {
                await Services.SheetManager.Host.HideAsync(editSheet);
            }
        };

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(editSheet);
        }
    }

    /// <summary>
    /// Seeks the player to the timestamp of the tapped lyric line.
    /// Uses the same pattern as the position slider: set CurrentPositionSeconds,
    /// then run the Seek command.
    /// </summary>
    private void OnLyricTapped(object? sender, TimedLyric lyric)
    {
        if (lyric == null || this.BindingContext is not Musicthingy.ViewModels.MainViewModel vm)
            return;

        vm.CurrentPositionSeconds = lyric.Time.TotalSeconds;

        if (vm.SeekCommand is System.Windows.Input.ICommand seek && seek.CanExecute(null))
            seek.Execute(null);
    }

    /// <summary>
    /// Event handler to open the Bottom Sheet Context Menu for the current track.
    /// </summary>
    private void OnCurrentTrackOptionsClicked(object? sender, EventArgs e)
    {
        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm && vm.CurrentTrack != null)
        {
            MenuHelper.ShowCurrentTrackOptions(vm.CurrentTrack, vm);
        }
    }

    // ===================== Header tap routing (click vs swipe) =====================
    // The header owns exactly ONE TapGestureRecognizer (HeaderDragZone, see
    // SetupGestures). It fires only for genuine clicks, so this handler receives
    // no events at all while the user swipes - even when the gesture starts on
    // the title. The click is then hit-tested against TrackTitleLabel: the
    // TITLE line only - the artist line below and the empty strip right of the
    // text do nothing. AutoMarqueeLabel.HitTestText accepts only points over
    // the rendered text extent (marquee-aware), and e.GetPosition returns the
    // point in the label's own coordinate space derived from the native view's
    // on-screen location, so the pane's translation shifts cannot skew it.

    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (_targetState == PlayerState.Mini)
        {
            // Mini keeps its tap-anywhere-on-the-header-to-expand behavior.
            AnimateToState(PlayerState.Medium);
            return;
        }

        var hit = e.GetPosition(TrackTitleLabel);
        if (hit.HasValue && TrackTitleLabel.HitTestText(hit.Value))
        {
            HandleTrackNameTap();
        }
    }

    /// <summary>
    /// Clicking the track name in the expanded header centers the track list
    /// on the currently playing track - the manual "scroll to track" trigger.
    /// From the full player it first
    /// smoothly collapses to the medium height so the list becomes visible
    /// behind the pane.
    /// </summary>
    private void HandleTrackNameTap()
    {
        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm)
        {
            // MobileMainPage listens for this and centers TracksListView.
            vm.ScrollToCurrentTrack();
        }

        if (_targetState == PlayerState.FullScreen)
        {
            // Smoothly collapse to medium; FinalizeVisibilityForState re-syncs
            // the lyrics to the active line once the animation lands.
            AnimateToState(PlayerState.Medium);
        }
    }

    // ===================== Bottom playback progress bar =====================
    // Purely visual, NON-interactive position indicator along the pane's very
    // bottom edge: InputTransparent in XAML (taps pass through to the pane),
    // no gestures, no time text. The fill width mirrors the expanded player's
    // slider bindings: progress = CurrentPositionSeconds / DurationSeconds.

    private INotifyPropertyChanged? _positionSourceVm;

    private void HookBottomProgressVm()
    {
        var vm = this.BindingContext as INotifyPropertyChanged;
        if (ReferenceEquals(vm, _positionSourceVm))
        {
            UpdateBottomProgress();
            return;
        }

        if (_positionSourceVm != null)
            _positionSourceVm.PropertyChanged -= OnPositionSourcePropertyChanged;

        _positionSourceVm = vm;
        if (_positionSourceVm != null)
            _positionSourceVm.PropertyChanged += OnPositionSourcePropertyChanged;

        UpdateBottomProgress();
    }

    private void OnPositionSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Musicthingy.ViewModels.MainViewModel.CurrentPositionSeconds) &&
            e.PropertyName != nameof(Musicthingy.ViewModels.MainViewModel.DurationSeconds))
            return;

        if (MainThread.IsMainThread)
            UpdateBottomProgress();
        else
            MainThread.BeginInvokeOnMainThread(UpdateBottomProgress);
    }

    private void UpdateBottomProgress()
    {
        double width = BottomProgressBar.Width;
        if (width <= 0)
            return; // not laid out yet - SizeChanged calls back once measured

        double progress = 0;
        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm && vm.DurationSeconds > 0)
            progress = Math.Clamp(vm.CurrentPositionSeconds / vm.DurationSeconds, 0, 1);

        double targetWidth = width * progress;
        if (Math.Abs(BottomProgressFill.WidthRequest - targetWidth) > 0.5)
            BottomProgressFill.WidthRequest = targetWidth;
    }

    // ==================== Mini-player track swipe (carousel) ====================
    // Swiping LEFT on the mini header skips FORWARD (the next track's page
    // slides in from the RIGHT edge); swiping RIGHT skips BACKWARD (the
    // previous track's page slides in from the LEFT edge). The neighbor pages
    // are full visual clones of the header page, kept up to date with the
    // queue, so during the swipe the current and the neighbor track are both
    // visible - like the pages were always there. The old swipe-right-to-close
    // behavior and the skip button are gone.

    private PlaylistItem? _swipeNext;
    private PlaylistItem? _swipePrev;
    private double _swipeTotalX;
    private bool _swipeSettling;
    private int _swipeToken;
    private INotifyPropertyChanged? _swipeSourceVm;
    private INotifyCollectionChanged? _swipeHookedQueue;

    private void HookSwipeVm()
    {
        var vm = this.BindingContext as INotifyPropertyChanged;
        if (!ReferenceEquals(vm, _swipeSourceVm))
        {
            if (_swipeSourceVm != null)
                _swipeSourceVm.PropertyChanged -= OnSwipeVmPropertyChanged;

            _swipeSourceVm = vm;
            if (_swipeSourceVm != null)
                _swipeSourceVm.PropertyChanged += OnSwipeVmPropertyChanged;

            _swipeHookedQueue = null; // re-resolve the queue for the new context
        }

        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel mvm &&
            !ReferenceEquals(_swipeHookedQueue, mvm.PlaybackQueue))
        {
            if (_swipeHookedQueue != null)
                _swipeHookedQueue.CollectionChanged -= OnSwipeQueueChanged;

            _swipeHookedQueue = mvm.PlaybackQueue;
            if (_swipeHookedQueue != null)
                _swipeHookedQueue.CollectionChanged += OnSwipeQueueChanged;
        }

        RefreshSwipePages();
    }

    private void OnSwipeVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Musicthingy.ViewModels.MainViewModel.CurrentTrack))
            RefreshSwipePages();
    }

    private void OnSwipeQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshSwipePages();

    /// <summary>
    /// Resolves which tracks the carousel should preview, mirroring the actual
    /// PlayNext/PlayPrevious behavior (wrap only with repeat on; shuffle
    /// reshuffles at the queue end so that side has no predictable preview).
    /// </summary>
    private void RefreshSwipePages()
    {
        _swipeNext = null;
        _swipePrev = null;

        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm &&
            vm.CurrentTrack != null && vm.PlaybackQueue != null)
        {
            int idx = vm.PlaybackQueue.IndexOf(vm.CurrentTrack);
            if (idx >= 0)
            {
                bool wraps = vm.RepeatState == Musicthingy.ViewModels.RepeatMode.All ||
                             vm.RepeatState == Musicthingy.ViewModels.RepeatMode.One;

                int next = idx + 1;
                if (next < vm.PlaybackQueue.Count)
                    _swipeNext = vm.PlaybackQueue[next];
                else if (wraps && !vm.IsShuffleEnabled && vm.PlaybackQueue.Count > 0)
                    _swipeNext = vm.PlaybackQueue[0]; // PlayNext wraps to the queue start

                int prev = idx - 1;
                if (prev >= 0)
                    _swipePrev = vm.PlaybackQueue[prev];
                else if (wraps && vm.PlaybackQueue.Count > 0)
                    _swipePrev = vm.PlaybackQueue[vm.PlaybackQueue.Count - 1]; // PlayPrevious wraps to the end
            }
        }

        PopulateSwipePage(NextTrackImage, NextTrackTitle, NextTrackArtist, _swipeNext);
        PopulateSwipePage(PrevTrackImage, PrevTrackTitle, PrevTrackArtist, _swipePrev);
    }

    private static void PopulateSwipePage(Image image, AutoMarqueeLabel title, AutoMarqueeLabel artist, PlaylistItem? item)
    {
        image.Source = item?.ItemIconSource;
        title.Text = item?.Title ?? string.Empty;
        artist.Text = item?.Artist ?? "Unknown Artist";
    }

    private double GetSwipePageWidth()
    {
        if (HeaderDragZone.Width > 1)
            return HeaderDragZone.Width;
        if (PlayerRoot.Width > 1)
            return PlayerRoot.Width;
        return 300;
    }

    /// <summary>Resting carousel state: header centered, pages off-screen + hidden.</summary>
    private void ResetSwipeGeometry()
    {
        _swipeToken++;          // invalidate any in-flight settle animation
        _swipeSettling = false;
        _swipeTotalX = 0;

        HeaderContentGrid.TranslationX = 0;
        double w = GetSwipePageWidth();
        NextTrackPage.TranslationX = w;
        PrevTrackPage.TranslationX = -w;
        NextTrackPage.IsVisible = false;
        PrevTrackPage.IsVisible = false;
    }

    /// <summary>
    /// ROUND 9: absolute state reconstruction after a rotation (width change).
    /// The pane's animations and gesture state were all captured against the
    /// OLD geometry; letting them run to completion leaves stale translations
    /// (the reported "title slowly drifting off-screen, never returning")
    /// because their callbacks keep writing into a coordinate space that no
    /// longer exists. This aborts every animated writer, resets every
    /// transient transform the pane owns, and snaps the pane to its CURRENT
    /// target state with a fresh layout pass - no animation, no incremental
    /// drift. Called only from the SizeChanged rotation branch, on the UI
    /// thread, where SizeChanged already runs.
    /// </summary>
    private void RebuildForRotation()
    {
        // 1. Kill every animated writer of pane geometry.
        this.AbortAnimation("PlayerResize");             // open/close snap in flight
        PlayerRoot.AbortAnimation("TranslateTo");        // close-drag settle / close flight
        this.AbortAnimation("PlayerClose");
        HeaderContentGrid.AbortAnimation("TranslateTo");  // carousel settle on the header
        NextTrackPage.AbortAnimation("TranslateTo");     // ...and its neighbor pages
        PrevTrackPage.AbortAnimation("TranslateTo");

        // 2. Drop any in-progress gesture: a pan captured against the old
        //    screen must not keep writing heights/translations once the
        //    geometry has changed underneath it. (Rotation mid-gesture is
        //    exactly the "title drifted and never returned" scenario.)
        //    Start() (not a Reset()) zeroes the velocity EMA + samples.
        _currentGesture = GestureIntent.None;
        _velocityTracker.Start();

        // 3. Reset every transient transform to its neutral value.
        PlayerRoot.TranslationY = 0;     // close-drag translation
        PlayerRoot.Scale = 1;            // parallax/scale experiments
        ResetSwipeGeometry();             // header + both carousel pages (uses new width)

        // 4. Snap the pane ABSOLUTELY to its current target state: fresh
        //    height, fresh safe-area padding, full layout pass, visibility
        //    finalized. No animation - the pane simply IS at its state.
        double targetHeight = _targetState switch
        {
            PlayerState.FullScreen => FullScreenHeight,
            PlayerState.Medium => MediumHeight,
            _ => MiniHeight
        };
        PlayerRoot.HeightRequest = targetHeight;
        UpdateLayoutForHeight(targetHeight);
        FinalizeVisibilityForState();

        // 5. Lyrics mode: the lyric view's centering/spacers were computed
        //    for the old viewport; a hard re-center (no animation) puts the
        //    active line back in the middle of the NEW geometry. Guarded by
        //    height>0 because the very first layout pass on the rotated
        //    geometry may not have measured the ScrollView yet - the
        //    LyricsView's own SizeChanged path re-runs this then.
        if (_isLyricsMode)
            LyricsZone.ScrollToActiveLyric(animate: false);
    }

    private void UpdateTrackSwipe(double totalX)
    {
        if (_swipeSettling)
            return;

        double pageWidth = GetSwipePageWidth();
        if (pageWidth <= 1)
            return;

        // Finger-following carousel: all three pages translate together.
        // A side with no neighbor track rubber-bands at quarter strength.
        double effective = totalX;
        if (totalX < 0 && _swipeNext == null) effective = totalX * 0.25;   // swipe left, no next
        if (totalX > 0 && _swipePrev == null) effective = totalX * 0.25;   // swipe right, no previous
        effective = Math.Clamp(effective, -pageWidth, pageWidth);

        HeaderContentGrid.TranslationX = effective;
        NextTrackPage.TranslationX = pageWidth + effective;   // enters from the right
        PrevTrackPage.TranslationX = -pageWidth + effective;  // enters from the left

        NextTrackPage.IsVisible = _swipeNext != null;
        PrevTrackPage.IsVisible = _swipePrev != null;
    }

    private void FinishTrackSwipe()
    {
        double pageWidth = GetSwipePageWidth();
        double moved = _swipeTotalX;
        double vx = _velocityTracker.VelocityX;
        bool forward = moved < 0; // swipe left -> next track
        bool hasTarget = forward ? _swipeNext != null : _swipePrev != null;

        // Commit on distance, or on a decisive flick in the swipe direction
        bool flick = Math.Abs(vx) > 1.0 && Math.Abs(moved) > 40 &&
                     Math.Sign(vx) == Math.Sign(moved);
        bool commit = hasTarget && pageWidth > 1 &&
                      (Math.Abs(moved) > pageWidth * 0.35 || flick);

        int token = ++_swipeToken;
        _swipeSettling = true;

        double currentTargetX = commit ? (forward ? -pageWidth : pageWidth) : 0;
        double neighborTargetX = commit ? 0 : (forward ? pageWidth : -pageWidth);
        var neighbor = forward ? (VisualElement)NextTrackPage : (VisualElement)PrevTrackPage;

        double remaining = Math.Abs((commit ? pageWidth : 0) - moved);
        uint duration = (uint)Math.Clamp(remaining / Math.Max(0.2, Math.Abs(vx)), 100, 250);

        var currentAnim = HeaderContentGrid.TranslateToAsync(currentTargetX, 0, duration, Easing.CubicOut);
        var neighborAnim = neighbor.TranslateToAsync(neighborTargetX, 0, duration, Easing.CubicOut);

        _ = Task.WhenAll(currentAnim, neighborAnim).ContinueWith(_ =>
        {
            if (token != _swipeToken)
                return; // superseded (new gesture / state change reset the geometry)

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (token != _swipeToken)
                    return;

                if (commit && this.BindingContext is Musicthingy.ViewModels.MainViewModel vm)
                {
                    // Switch the actual playback while the neighbor page still
                    // covers the header - the header rebinds to the new track
                    // underneath, so the swap is never visible.
                    if (forward) vm.PlayNext();
                    else vm.PlayPrevious();
                }

                // Hard reset to the resting carousel state. When committed,
                // the neighbor page at center is pixel-identical to the freshly
                // rebound header underneath, so this is invisible.
                HeaderContentGrid.TranslationX = 0;
                double w = GetSwipePageWidth();
                NextTrackPage.TranslationX = w;
                PrevTrackPage.TranslationX = -w;
                NextTrackPage.IsVisible = false;
                PrevTrackPage.IsVisible = false;
                _swipeSettling = false;

                RefreshSwipePages();
            });
        });
    }
}
