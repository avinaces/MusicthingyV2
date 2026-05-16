using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Musicthingy.Models;
using System;
using System.Linq;

namespace Musicthingy.Views;

public partial class MobilePlayerPaneView : ContentView
{
    private enum PlayerState { Mini, Medium, FullScreen }
    private enum GestureIntent { None, Resizing, ClosingRight, ClosingDown }

    private PlayerState _targetState = PlayerState.Mini;
    private GestureIntent _currentGesture = GestureIntent.None;
    private double _startPanHeight;

    private const double MiniHeight = 72;
    private const double MediumHeight = 280;

    public MobilePlayerPaneView()
    {
        InitializeComponent();
        SetupGestures();

        UpdateLayoutForHeight(MiniHeight);

        WeakReferenceMessenger.Default.Register<ActiveLyricChangedMessage>(this, (r, m) =>
        {
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
                break;

            case GestureStatus.Running:
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
                    if (PlayerRoot.TranslationX > 120) ClosePlayer(true);
                    else
                    {
                        PlayerRoot.TranslateToAsync(0, 0, 250, Easing.CubicOut);
                        SnapToBestState();
                    }
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    if (PlayerRoot.TranslationY > 50) ClosePlayer(false);
                    else
                    {
                        PlayerRoot.TranslateToAsync(0, 0, 250, Easing.CubicOut);
                        SnapToBestState();
                    }
                }
                else
                {
                    SnapToBestState();
                }

                _currentGesture = GestureIntent.None;
                break;
        }
    }

    private void UpdateLayoutForHeight(double height)
    {
        double progressToMedium = Math.Clamp((height - MiniHeight) / (MediumHeight - MiniHeight), 0, 1);

        double imgSize = 44 + (24 * progressToMedium);
        TrackImageBorder.WidthRequest = imgSize;
        TrackImageBorder.HeightRequest = imgSize;

        double miniOpacity = Math.Clamp(1.0 - (progressToMedium / 0.3), 0, 1);
        MiniControls.Opacity = miniOpacity;
        MiniControls.IsVisible = miniOpacity > 0.01;

        double expandedOpacity = Math.Clamp((progressToMedium - 0.3) / 0.7, 0, 1);
        ExpandedHeaderControls.Opacity = expandedOpacity;
        ExpandedHeaderControls.IsVisible = expandedOpacity > 0.01;

        DragHandle.Opacity = expandedOpacity;
        DragHandle.IsVisible = expandedOpacity > 0.01;

        ExpandedContentZone.Opacity = expandedOpacity;
        ExpandedContentZone.IsVisible = height > (MiniHeight + 5);

        // 5. Smoothly morphed MARGIN instead of Padding to prevent Grid snapping
        double marginLr = 12 + (4 * progressToMedium);        // 12 -> 16
        double marginTop = 14 + (12 * progressToMedium);      // 14 -> 26
        double marginBot = 14 - (6 * progressToMedium);       // 14 -> 8
        HeaderContentGrid.Margin = new Thickness(marginLr, marginTop, marginLr, marginBot);

        double fullHeight = FullScreenHeight;
        if (fullHeight > MediumHeight && NoLyricsPrompt != null)
        {
            double progressToFull = Math.Clamp((height - MediumHeight) / (fullHeight - MediumHeight), 0, 1);
            double promptOpacity = Math.Clamp(progressToFull * 2, 0, 1);

            NoLyricsPrompt.Opacity = promptOpacity;
            NoLyricsPrompt.IsVisible = promptOpacity > 0.01;
        }
    }

    private async void ClosePlayer(bool isHorizontal)
    {
        uint length = 200;

        if (isHorizontal)
        {
            await PlayerRoot.TranslateToAsync(1000, 0, length, Easing.CubicIn);
        }
        else
        {
            await PlayerRoot.TranslateToAsync(0, MiniHeight + 100, length, Easing.CubicIn);
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
    }

    private void SnapToBestState()
    {
        double currentHeight = PlayerRoot.Height;

        if (currentHeight > (FullScreenHeight * 0.6)) AnimateToState(PlayerState.FullScreen);
        else if (currentHeight > (MiniHeight + 80)) AnimateToState(PlayerState.Medium);
        else AnimateToState(PlayerState.Mini);
    }

    private void AnimateToState(PlayerState state)
    {
        _targetState = state;
        double targetHeight = state switch
        {
            PlayerState.FullScreen => FullScreenHeight,
            PlayerState.Medium => MediumHeight,
            _ => MiniHeight
        };

        Animation heightAnimation = new Animation(
            callback: h =>
            {
                PlayerRoot.HeightRequest = h;
                UpdateLayoutForHeight(h);
            },
            start: PlayerRoot.Height,
            end: targetHeight,
            easing: Easing.CubicOut);

        heightAnimation.Commit(this, "PlayerResize", length: 250);
    }
}