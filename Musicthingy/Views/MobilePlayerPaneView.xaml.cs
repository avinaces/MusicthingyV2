using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Musicthingy.Models;
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
    private const double MediumHeight = 280;

    public System.Windows.Input.ICommand OpenTagsCommand { get; }

    private readonly PanVelocityTracker _velocityTracker = new();

    public MobilePlayerPaneView()
    {
        InitializeComponent();
        SetupGestures();

        UpdateLayoutForHeight(MiniHeight);

        OpenTagsCommand = new Command<PlaylistItem>(OnOpenTagsClicked);

        // Tapping a timed lyric line seeks the player to that timestamp
        LyricsZone.LyricTapped += OnLyricTapped;

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
                if (_targetState != PlayerState.Mini && m.Lyric != null)
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
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startPanHeight = PlayerRoot.Height;
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

                    PlayerRoot.HeightRequest = newHeight;
                    UpdateLayoutForHeight(newHeight);
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

        if (velocity > 0.1) length = (uint)Math.Clamp((MiniHeight + 100 - PlayerRoot.TranslationY) / velocity, 100, 350);
        await PlayerRoot.TranslateToAsync(0, MiniHeight + 100, length, Easing.CubicOut);

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
        DragHandle.IsVisible = true;
        ExpandedContentZone.IsVisible = true;
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
        DragHandle.IsVisible = DragHandle.Opacity > 0.01;
        ExpandedContentZone.IsVisible = PlayerRoot.HeightRequest > (MiniHeight + 5);

        LyricsZone.FinalizeVisibility();

        // Re-sync lyrics to current position now that the animation has safely completed
        if (_targetState != PlayerState.Mini)
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

        // The zone is anchored (VerticalOptions=Start) to the TOP of the pane
        // and pushed down by the header shift (TranslationY further below), so
        // its height must be the pane height MINUS that shift - its bottom then
        // lands exactly on the pane's bottom edge when fully expanded. The old
        // full-height value made the zone overshoot past the screen bottom by
        // the header height, pushing the lyric viewport - and its scrollbar -
        // off screen and skewing every centering/spacer math below it.
        // Uses the FULL-EXPANSION header height (72 + 30), not the animated
        // one, so the zone's height stays constant during the Mini->Medium
        // transition instead of forcing a full relayout every animation frame.
        const double ExpandedHeaderShift = 102;
        double zoneHeight = Math.Max(0, fullHeight - ExpandedHeaderShift);
        if (ExpandedContentZone.HeightRequest < 0 || Math.Abs(ExpandedContentZone.HeightRequest - zoneHeight) > 1)
        {
            ExpandedContentZone.HeightRequest = zoneHeight;
        }

        double imgSize = 44 + (24 * progressToMedium);
        TrackImageBorder.WidthRequest = imgSize;
        TrackImageBorder.HeightRequest = imgSize;

        // The skip button is gone from the mini header (replaced by the swipe
        // carousel) - only the play/pause button (40px) remains on the right.
        HeaderRightControls.WidthRequest = 40;

        MiniControls.Opacity = Math.Clamp(1.0 - (progressToMedium / 0.3), 0, 1);

        // Fade IN the overall Expanded controls container during Mini->Medium
        double expandedOpacity = Math.Clamp((progressToMedium - 0.3) / 0.7, 0, 1);
        ExpandedHeaderControls.Opacity = expandedOpacity;

        DragHandle.Opacity = expandedOpacity;
        ExpandedContentZone.Opacity = expandedOpacity;

        // Bottom progress bar is a MINI-state-only element: the expanded
        // states have the real position slider instead.
        BottomProgressBar.IsVisible = progressToMedium <= 0;

        // Keep horizontal margins constant so the text block bounds don't jitter
        double marginTop = 14 + (12 * progressToMedium);
        double marginBot = 14 - (6 * progressToMedium);
        HeaderContentGrid.Margin = new Thickness(16, marginTop, 16, marginBot);

        // Keep the swipe preview pages pixel-aligned with the header page so
        // the carousel reset after a committed swipe is invisible.
        var headerMargin = HeaderContentGrid.Margin;
        PrevTrackPage.Margin = headerMargin;
        NextTrackPage.Margin = headerMargin;

        double headerHeight = 72 + (30 * progressToMedium);
        ExpandedContentZone.TranslationY = headerHeight;

        if (fullHeight > MediumHeight)
        {
            LyricsZone.UpdateForFullScreenProgress(progressToFull);
        }
    }

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
