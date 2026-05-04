using Microsoft.Maui.Controls;
using System;

namespace Musicthingy.Views;

public partial class MobilePlayerPaneView : ContentView
{
    private enum PlayerState { Mini, Medium, FullScreen }

    private PlayerState _targetState = PlayerState.Mini;
    private double _startPanHeight;

    // Fixed Height Constants
    private const double MiniHeight = 72;
    private const double MediumHeight = 280;

    // Safely fetch the max screen height dynamically instead of relying on this.Height
    private double FullScreenHeight
    {
        get
        {
            var parent = this.Parent as VisualElement;
            if (parent != null && parent.Height > 0)
                return parent.Height;

            return Application.Current?.MainPage?.Height ?? 800;
        }
    }

    public MobilePlayerPaneView()
    {
        InitializeComponent();
        SetupGestures();
    }

    private void SetupGestures()
    {
        // Smooth Dragging Pan Gesture
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;

        // Tap Header to Expand
        var tap = new TapGestureRecognizer();
        tap.Tapped += (s, e) =>
        {
            if (_targetState == PlayerState.Mini)
                AnimateToState(PlayerState.Medium);
        };

        // Important: Attach the gestures ONLY to the header zone, 
        // preventing conflicts with the Lyrics ScrollView and Sliders!
        HeaderDragZone.GestureRecognizers.Clear();
        HeaderDragZone.GestureRecognizers.Add(pan);
        HeaderDragZone.GestureRecognizers.Add(tap);
    }

    private void OnPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startPanHeight = PlayerRoot.Height;
                break;

            case GestureStatus.Running:
                // e.TotalY is negative when dragging UP, so we subtract it from start height
                double newHeight = _startPanHeight - e.TotalY;

                // Keep the height contained within limits
                newHeight = Math.Clamp(newHeight, MiniHeight, FullScreenHeight);

                PlayerRoot.HeightRequest = newHeight;

                // Seamlessly toggle UI elements as it resizes
                UpdateVisualStateForHeight(newHeight);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                SnapToBestState();
                break;
        }
    }

    private void UpdateVisualStateForHeight(double height)
    {
        string stateName;
        if (height < (MiniHeight + 50))
            stateName = "Mini";
        else if (height < (FullScreenHeight * 0.75))
            stateName = "Medium";
        else
            stateName = "FullScreen";

        VisualStateManager.GoToState(PlayerRoot, stateName);
    }

    private void SnapToBestState()
    {
        double currentHeight = PlayerRoot.Height;

        // Determine which state is closest to where the user let go
        if (currentHeight > (FullScreenHeight * 0.6))
            AnimateToState(PlayerState.FullScreen);
        else if (currentHeight > (MiniHeight + 80))
            AnimateToState(PlayerState.Medium);
        else
            AnimateToState(PlayerState.Mini);
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

        // Animate the layout change smoothly
        Animation heightAnimation = new Animation(
            callback: h => PlayerRoot.HeightRequest = h,
            start: PlayerRoot.Height,
            end: targetHeight,
            easing: Easing.CubicOut);

        heightAnimation.Commit(this, "PlayerResize", length: 250, finished: (v, c) =>
        {
            UpdateVisualStateForHeight(targetHeight);
        });
    }
}