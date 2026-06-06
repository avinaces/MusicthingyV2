using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.ViewModels;

namespace Musicthingy.Views;

public partial class MobileMainPage : ContentPage
{
    private bool _isProgrammaticScroll = false;
    private double _lastScrollY = 0;
    private CancellationTokenSource? _scrollCts;

    // Custom Sidebar interaction variables
    private double _panStartX = 0;
    private bool _isSidebarOpen = false;
    private bool _isSidebarPanning = false;

    private readonly PanVelocityTracker _sidebarVelocityTracker = new();

    // Dynamic width lookup handling full-screen bounds safely
    private double SidebarWidth => this.Width > 0 ? this.Width : 360;

    // Tracks the height of the playlist header for sticky toolbar logic
    private double _playlistHeaderHeight = 0;

    public MobileMainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        Services.SheetManager.Host = GlobalSheetHost;

        WeakReferenceMessenger.Default.Register<ScrollToTrackMessage>(this, (r, m) =>
        {
            Task.Run(async () =>
            {
                _isProgrammaticScroll = true;

                await Task.Delay(150);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        TracksListView?.ScrollToItem(m.Track, ScrollToPosition.Center, false);
                    }
                    catch { }
                });

                ResetScrollDetection();
            });
        });
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (!_isSidebarOpen && width > 0)
        {
            SidebarGrid.TranslationX = -width;
        }
    }

    private void ResetScrollDetection()
    {
        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();

        CancellationToken token = _scrollCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                _isProgrammaticScroll = false;
            }
            catch (TaskCanceledException) { }
        });
    }

    // --- Sticky Toolbar Calculations ---

    private void OnPlaylistInfoSizeChanged(object? sender, EventArgs e)
    {
        if (sender is View view && view.Height > 0)
        {
            _playlistHeaderHeight = view.Height;
            UpdateStickyToolbar();
        }
    }

    private void UpdateStickyToolbar()
    {
        if (_playlistHeaderHeight > 0)
        {
            // If the scroll position goes past the bottom of the playlist info, 
            // the inline toolbar hits the top of the screen. We hide the inline one 
            // by scrolling past it naturally, and make the Sticky one visible to take its place!
            StickyToolbar.IsVisible = _lastScrollY >= _playlistHeaderHeight;
        }
    }

    // --- Custom Sidebar Interaction Logic ---

    private void OnEdgePanUpdated(object? sender, PanUpdatedEventArgs e) => HandleSidebarPan(e);
    private void OnSidebarPanUpdated(object? sender, PanUpdatedEventArgs e) => HandleSidebarPan(e);

    private void HandleSidebarPan(PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isSidebarPanning = true;
                SidebarDarken.IsVisible = true;
                EdgeSwipeZone.IsVisible = false;
                _panStartX = SidebarGrid.TranslationX;

                if (_panStartX < -SidebarWidth) _panStartX = -SidebarWidth;

                _sidebarVelocityTracker.Start();
                break;

            case GestureStatus.Running:
                if (!_isSidebarPanning) return;

                _sidebarVelocityTracker.Update(e.TotalX, 0);

                double newTx = _panStartX + e.TotalX;
                if (newTx > 0) newTx = 0;
                if (newTx < -SidebarWidth) newTx = -SidebarWidth;

                SidebarGrid.TranslationX = newTx;
                SidebarDarken.Opacity = (newTx + SidebarWidth) / SidebarWidth;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isSidebarPanning = false;

                double velX = _sidebarVelocityTracker.VelocityX;
                double movedX = SidebarGrid.TranslationX - _panStartX;

                bool shouldOpen = false;
                if (velX > 1.0 && movedX > 40)
                    shouldOpen = true;
                else if (velX < -1.0 && movedX < -40)
                    shouldOpen = false;
                else
                    shouldOpen = SidebarGrid.TranslationX > -(SidebarWidth * 0.5);

                if (shouldOpen)
                    _ = OpenSidebarInternalAsync(velX);
                else
                    _ = CloseSidebarInternalAsync(velX);
                break;
        }
    }

    private void OnCloseSidebarClicked(object? sender, EventArgs e) => _ = CloseSidebarInternalAsync();
    private void OnSidebarOverlayTapped(object? sender, TappedEventArgs e) => _ = CloseSidebarInternalAsync();
    private void OnHamburgerTapped(object? sender, TappedEventArgs e) => _ = OpenSidebarInternalAsync();

    private async Task OpenSidebarInternalAsync(double velocityX = 0)
    {
        _isSidebarOpen = true;
        SidebarDarken.IsVisible = true;
        EdgeSwipeZone.IsVisible = false;

        uint duration = 250;
        double distance = Math.Abs(SidebarGrid.TranslationX);
        if (velocityX > 0.1) duration = (uint)Math.Clamp(distance / velocityX, 100, 350);

        _ = SidebarDarken.FadeToAsync(1, duration, Easing.CubicOut);
        await SidebarGrid.TranslateToAsync(0, 0, duration, Easing.CubicOut);
    }

    private async Task CloseSidebarInternalAsync(double velocityX = 0)
    {
        _isSidebarOpen = false;

        uint duration = 250;
        double distance = Math.Abs(-SidebarWidth - SidebarGrid.TranslationX);
        if (velocityX < -0.1) duration = (uint)Math.Clamp(distance / Math.Abs(velocityX), 100, 350);

        _ = SidebarDarken.FadeToAsync(0, duration, Easing.CubicOut);
        await SidebarGrid.TranslateToAsync(-SidebarWidth, 0, duration, Easing.CubicOut);

        SidebarDarken.IsVisible = false;
        EdgeSwipeZone.IsVisible = true;
    }

    protected override bool OnBackButtonPressed()
    {
        if (_isSidebarOpen)
        {
            _ = CloseSidebarInternalAsync();
            return true;
        }
        return base.OnBackButtonPressed();
    }
    // ----------------------------------------------

    private async void OnPlaylistTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
                vm.SelectedPlaylist = playlist;
            }
            await CloseSidebarInternalAsync();
        }
    }

    private void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
                vm.CurrentTrack = track;
            }
        }
    }

    private void OnListScrolled(object? sender, ScrolledEventArgs e)
    {
        if (_isProgrammaticScroll)
        {
            _lastScrollY = e.ScrollY;
            ResetScrollDetection();
            UpdateStickyToolbar();
            return;
        }

        if (Math.Abs(e.ScrollY - _lastScrollY) > 5)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
            }
        }

        _lastScrollY = e.ScrollY;
        UpdateStickyToolbar();
    }

    private void OnTrackOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                if (track == vm.CurrentTrack)
                {
                    MenuHelper.ShowCurrentTrackOptions(track, vm);
                }
                else
                {
                    MenuHelper.ShowTrackOptions(track, vm);
                }
            }
        }
    }

    private void OnPlaylistOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                if (playlist == vm.CurrentTrack?.Playlist)
                {
                    MenuHelper.ShowCurrentPlaylistOptions(playlist, vm);
                }
                else
                {
                    MenuHelper.ShowPlaylistOptions(playlist, vm);
                }
            }
        }
    }

    private async void OnSortOptionsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
        {
            var sortSheet = new SortOptionsSheet(vm);

            if (Services.SheetManager.Host != null)
            {
                await Services.SheetManager.Host.ShowAsync(sortSheet);
            }
        }
    }

}