using Microsoft.Maui.Controls;
using System;
using System.Linq;

namespace Musicthingy.Views;

public partial class MobilePlayerPaneView : ContentView
{
    private enum PlayerState { Mini, Medium, FullScreen }

    // Tracks the exact intended action so gestures don't conflict midway through a swipe
    private enum GestureIntent { None, Resizing, ClosingRight, ClosingDown }

    private PlayerState _targetState = PlayerState.Mini;
    private GestureIntent _currentGesture = GestureIntent.None;
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
            var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            return activePage?.Height ?? 800;
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

    private void OnPanUpdated(object ?sender, PanUpdatedEventArgs e)
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
                    // Require a minimum threshold to determine gesture direction
                    if (Math.Abs(e.TotalX) > 15 || Math.Abs(e.TotalY) > 15)
                    {
                        if (_startPanHeight <= MiniHeight + 1)
                        {
                            // 1. Prioritize Resizing Upwards (Prevents accidental closes when swiping up diagonally)
                            if (e.TotalY < -10 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX) * 0.5)
                            {
                                _currentGesture = GestureIntent.Resizing;
                            }
                            // 2. Closing Right
                            else if (e.TotalX > 20 && Math.Abs(e.TotalX) > Math.Abs(e.TotalY))
                            {
                                _currentGesture = GestureIntent.ClosingRight;
                            }
                            // 3. Closing Down
                            else if (e.TotalY > 20 && Math.Abs(e.TotalY) > Math.Abs(e.TotalX))
                            {
                                _currentGesture = GestureIntent.ClosingDown;
                            }
                        }
                        else
                        {
                            // If we are already expanded, any significant movement is resizing. We block closing.
                            _currentGesture = GestureIntent.Resizing;
                        }
                    }
                }

                // Apply layout updates based exclusively on the locked intent
                if (_currentGesture == GestureIntent.ClosingRight)
                {
                    if (e.TotalX > 0)
                        PlayerRoot.TranslationX = e.TotalX;
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    if (e.TotalY > 0)
                        PlayerRoot.TranslationY = e.TotalY;
                }
                else if (_currentGesture == GestureIntent.Resizing)
                {
                    double newHeight = _startPanHeight - e.TotalY;
                    newHeight = Math.Clamp(newHeight, MiniHeight, FullScreenHeight);
                    PlayerRoot.HeightRequest = newHeight;
                    UpdateVisualStateForHeight(newHeight);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:

                if (_currentGesture == GestureIntent.ClosingRight)
                {
                    // Require a deliberate swipe distance to commit to closing
                    if (PlayerRoot.TranslationX > 120)
                        ClosePlayer(true);
                    else
                    {
                        PlayerRoot.TranslateToAsync(0, 0, 250, Easing.CubicOut);
                        SnapToBestState();
                    }
                }
                else if (_currentGesture == GestureIntent.ClosingDown)
                {
                    if (PlayerRoot.TranslationY > 50)
                        ClosePlayer(false);
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

                // Reset intent for the next swipe
                _currentGesture = GestureIntent.None;
                break;
        }
    }

    private async void ClosePlayer(bool isHorizontal)
    {
        uint length = 200;

        // Animate completely off-screen using explicit large coordinates
        if (isHorizontal)
        {
            await PlayerRoot.TranslateToAsync(1000, 0, length, Easing.CubicIn);
        }
        else
        {
            await PlayerRoot.TranslateToAsync(0, MiniHeight + 100, length, Easing.CubicIn);
        }

        // Stop playback, hide pane, clear queue, and deselect the track
        if (this.BindingContext is Musicthingy.ViewModels.MainViewModel vm)
        {
            // 1. Manually deselect the track so it doesn't stay visually highlighted in the list
            if (vm.CurrentTrack != null)
            {
                vm.CurrentTrack.IsSelected = false;
            }

            // 2. Set to null to stop VLC and hide the UI pane
            vm.CurrentTrack = null;

            // 3. Clear the queue for a perfectly clean slate
            vm.PlaybackQueue?.Clear();

            // 4. Wipe the saved state from Preferences so the closed track doesn't resume on the next app launch
            Microsoft.Maui.Storage.Preferences.Default.Remove("CurrentTrackData");
            Microsoft.Maui.Storage.Preferences.Default.Remove("PlaybackQueueData");
        }

        // Reset translation properties invisibly so it behaves normally the next time a track is clicked
        PlayerRoot.TranslationX = 0;
        PlayerRoot.TranslationY = 0;
        PlayerRoot.HeightRequest = MiniHeight;
        _targetState = PlayerState.Mini;
        UpdateVisualStateForHeight(MiniHeight);
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