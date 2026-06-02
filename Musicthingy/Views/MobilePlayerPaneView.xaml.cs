using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Musicthingy.Models;
using System;
using System.Linq;
using Musicthingy.Helpers;

namespace Musicthingy.Views;

public partial class MobilePlayerPaneView : ContentView
{
    private enum PlayerState { Mini, Medium, FullScreen }
    private enum GestureIntent { None, Resizing, ClosingRight, ClosingDown }

    private PlayerState _targetState = PlayerState.Mini;
    private GestureIntent _currentGesture = GestureIntent.None;
    private double _startPanHeight;

    // Flag to pause layout-heavy actions (like ScrollTo) during 60fps animations
    private bool _isAnimating;

    private const double MiniHeight = 72;
    private const double MediumHeight = 280;

    private readonly PanVelocityTracker _velocityTracker = new();

    public MobilePlayerPaneView()
    {
        InitializeComponent();
        SetupGestures();

        UpdateLayoutForHeight(MiniHeight);

        WeakReferenceMessenger.Default.Register<ActiveLyricChangedMessage>(this, (r, m) =>
        {
            if (_isAnimating || _currentGesture != GestureIntent.None)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_targetState != PlayerState.Mini && m.Lyric != null)
                {
                    LyricsCollectionView.ScrollTo(m.Lyric, position: ScrollToPosition.Center, animate: true);
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

        var tap = new TapGestureRecognizer();
        tap.Tapped += (s, e) =>
        {
            if (_targetState == PlayerState.Mini)
                AnimateToState(PlayerState.Medium);
        };

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
                            if (e.TotalY < -10 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX) * 0.5)
                                _currentGesture = GestureIntent.Resizing;
                            else if (e.TotalX > 20 && Math.Abs(e.TotalX) > Math.Abs(e.TotalY))
                                _currentGesture = GestureIntent.ClosingRight;
                            else if (e.TotalY > 20 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX))
                                _currentGesture = GestureIntent.ClosingDown;
                        }
                        else
                        {
                            _currentGesture = GestureIntent.Resizing;
                        }
                    }
                }

                if (_currentGesture == GestureIntent.ClosingRight)
                {
                    if (e.TotalX > 0) PlayerRoot.TranslationX = e.TotalX;
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
                if (_currentGesture == GestureIntent.ClosingRight)
                {
                    // Require at least 40px of panning to count as a flick close
                    if (PlayerRoot.TranslationX > 120 || (_velocityTracker.VelocityX > 1.0 && PlayerRoot.TranslationX > 40)) ClosePlayer(true, _velocityTracker.VelocityX);
                    else
                    {
                        uint duration = 250;
                        if (_velocityTracker.VelocityX < -0.1) duration = (uint)Math.Clamp(Math.Abs(PlayerRoot.TranslationX / _velocityTracker.VelocityX), 100, 350);
                        PlayerRoot.TranslateToAsync(0, 0, duration, Easing.CubicOut);
                        SnapToBestState(_velocityTracker.VelocityY, 0);
                    }
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    // Require at least 40px of panning to count as a flick close
                    if (PlayerRoot.TranslationY > 50 || (_velocityTracker.VelocityY > 1.0 && PlayerRoot.TranslationY > 40)) ClosePlayer(false, _velocityTracker.VelocityY);
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

    private async void ClosePlayer(bool isHorizontal, double velocity = 0)
    {
        _isAnimating = true;
        uint length = 200;

        if (isHorizontal)
        {
            if (velocity > 0.1) length = (uint)Math.Clamp((1000 - PlayerRoot.TranslationX) / velocity, 100, 350);
            await PlayerRoot.TranslateToAsync(1000, 0, length, Easing.CubicOut); // Fixed Easing
        }
        else
        {
            if (velocity > 0.1) length = (uint)Math.Clamp((MiniHeight + 100 - PlayerRoot.TranslationY) / velocity, 100, 350);
            await PlayerRoot.TranslateToAsync(0, MiniHeight + 100, length, Easing.CubicOut); // Fixed Easing
        }

        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm)
        {
            if (vm.CurrentTrack != null) vm.CurrentTrack.IsSelected = false;
            vm.CurrentTrack = null;
            vm.PlaybackQueue?.Clear();
        }

        PlayerRoot.TranslationX = 0;
        PlayerRoot.TranslationY = 0;
        PlayerRoot.HeightRequest = MiniHeight;
        _targetState = PlayerState.Mini;

        UpdateLayoutForHeight(MiniHeight);
        FinalizeVisibilityForState();
    }

    private void PrepareVisibilityForAnimation()
    {
        _isAnimating = true;
        MiniControls.IsVisible = true;
        ExpandedHeaderControls.IsVisible = true;
        DragHandle.IsVisible = true;
        ExpandedContentZone.IsVisible = true;
        if (NoLyricsPrompt != null) NoLyricsPrompt.IsVisible = true;
    }

    private void FinalizeVisibilityForState()
    {
        _isAnimating = false;

        // Only safely update IsVisible when the animation has completely stopped
        MiniControls.IsVisible = MiniControls.Opacity > 0.01;
        ExpandedHeaderControls.IsVisible = ExpandedHeaderControls.Opacity > 0.01;
        DragHandle.IsVisible = DragHandle.Opacity > 0.01;
        ExpandedContentZone.IsVisible = PlayerRoot.HeightRequest > (MiniHeight + 5);

        if (NoLyricsPrompt != null)
            NoLyricsPrompt.IsVisible = NoLyricsPrompt.Opacity > 0.01;

        // Re-sync lyrics to current position now that the animation has safely completed
        if (_targetState != PlayerState.Mini)
        {
            var activeLyric = LyricsCollectionView.ItemsSource?.Cast<TimedLyric>().FirstOrDefault(l => l.IsActive);
            if (activeLyric != null)
                LyricsCollectionView.ScrollTo(activeLyric, position: ScrollToPosition.Center, animate: true);
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

        if (ExpandedContentZone.HeightRequest < 0 || Math.Abs(ExpandedContentZone.HeightRequest - fullHeight) > 1)
        {
            ExpandedContentZone.HeightRequest = fullHeight;
        }

        double imgSize = 44 + (24 * progressToMedium);
        TrackImageBorder.WidthRequest = imgSize;
        TrackImageBorder.HeightRequest = imgSize;

        // Smoothly shrink the right container from 84px (Mini) -> 40px (Medium & Full).
        // This compensates for the image growing, giving the title text extra room so it never shortens.
        double rightWidth = 84 - (44 * progressToMedium);
        HeaderRightControls.WidthRequest = Math.Max(0, rightWidth);

        MiniControls.Opacity = Math.Clamp(1.0 - (progressToMedium / 0.3), 0, 1);

        // Fade IN the overall Expanded controls container during Mini->Medium
        double expandedOpacity = Math.Clamp((progressToMedium - 0.3) / 0.7, 0, 1);
        ExpandedHeaderControls.Opacity = expandedOpacity;

        // Crossfade between AutoLocate and Track Options during Medium->Full
        double fullScreenFadeOut = Math.Clamp(1.0 - (progressToFull / 0.3), 0, 1);
        AutoFollowBtn.Opacity = fullScreenFadeOut;

        // Fade in Options button only when progressing to Full Screen
        double optionsFadeIn = Math.Clamp(progressToFull / 0.3, 0, 1);
        TrackOptionsBtn.Opacity = optionsFadeIn;

        // Prevent clicking invisible buttons
        AutoFollowBtn.InputTransparent = progressToFull > 0.5;
        TrackOptionsBtn.InputTransparent = progressToFull <= 0.5;

        DragHandle.Opacity = expandedOpacity;
        ExpandedContentZone.Opacity = expandedOpacity;

        // Keep horizontal margins constant so the text block bounds don't jitter
        double marginTop = 14 + (12 * progressToMedium);
        double marginBot = 14 - (6 * progressToMedium);
        HeaderContentGrid.Margin = new Thickness(16, marginTop, 16, marginBot);

        // 🚀 GPU Optimization
        double headerHeight = 72 + (30 * progressToMedium);
        ExpandedContentZone.TranslationY = headerHeight;

        if (fullHeight > MediumHeight && NoLyricsPrompt != null)
        {
            NoLyricsPrompt.Opacity = Math.Clamp(progressToFull * 2, 0, 1);
        }
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
}