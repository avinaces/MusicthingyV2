using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Search;
using Musicthingy.Settings;
using Musicthingy.ViewModels;
using System.ComponentModel;

namespace Musicthingy.Views;

public partial class MobileMainPage : ContentPage
{
    private double _lastScrollY = 0;

    // Custom Sidebar interaction variables
    private double _panStartX = 0;
    private bool _isSidebarOpen = false;
    private bool _isSidebarPanning = false;

    private readonly PanVelocityTracker _sidebarVelocityTracker = new();

    // Dynamic width lookup handling full-screen bounds safely
    private double SidebarWidth => this.Width > 0 ? this.Width : 360;

    // Tracks the height of the playlist header for sticky toolbar logic
    private double _playlistHeaderHeight = 0;

    private readonly SearchHostViewModel _mainSearchHost;
    private readonly SearchHostViewModel _sidebarSearchHost;
    private readonly MainViewModel _viewModel;

    public MobileMainPage(MainViewModel viewModel, SearchService searchService)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;

        Services.SheetManager.Host = GlobalSheetHost;

        _mainSearchHost = new SearchHostViewModel(searchService, SearchOrigin.OpenPlaylist)
        {
            Placeholder = "Search tracks"
        };
        _sidebarSearchHost = new SearchHostViewModel(searchService, SearchOrigin.PlaylistSidebar)
        {
            Placeholder = "Search playlists"
        };

        MainSearchBar.BindingContext = _mainSearchHost;
        SidebarSearchBar.BindingContext = _sidebarSearchHost;
        MainSearchResults.BindingContext = _mainSearchHost;
        SidebarSearchResults.BindingContext = _sidebarSearchHost;

        MainSearchBar.SetBinding(SearchBarView.QueryProperty, new Binding(nameof(SearchHostViewModel.Query), BindingMode.TwoWay, source: _mainSearchHost));
        SidebarSearchBar.SetBinding(SearchBarView.QueryProperty, new Binding(nameof(SearchHostViewModel.Query), BindingMode.TwoWay, source: _sidebarSearchHost));
        SidebarSearchBar.Placeholder = _sidebarSearchHost.Placeholder;

        _mainSearchHost.ResultActivated += OnSearchResultActivated;
        _sidebarSearchHost.ResultActivated += OnSearchResultActivated;
        _mainSearchHost.Dismissed += (_, _) => MainSearchBar.Blur();
        _sidebarSearchHost.Dismissed += (_, _) => SidebarSearchBar.Blur();
        _mainSearchHost.PropertyChanged += OnSearchHostPropertyChanged;
        _sidebarSearchHost.PropertyChanged += OnSearchHostPropertyChanged;

        MainSearchBar.SearchFocused += OnMainSearchFocused;
        SidebarSearchBar.SearchFocused += OnSidebarSearchFocused;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateSearchPlaceholders();

        // Start the drawer one width to the left; the XAML -5000 sentinel is
        // only there to hide it before the page has a width, and animating from
        // it on the first hamburger tap could be skipped entirely.
        SidebarGrid.TranslationX = -SidebarWidth;

        SizeChanged += (_, _) =>
        {
            UpdateSearchHeights();
            if (!_isSidebarOpen && !_isSidebarPanning)
                SidebarGrid.TranslationX = -SidebarWidth;
        };
        UpdateSearchHeights();

        WeakReferenceMessenger.Default.Register<ScrollToTrackMessage>(this, (r, m) =>
        {
            // Small delay so a playlist switch (fired together with this
            // message) can finish rebinding the list first.
            Task.Run(async () =>
            {
                await Task.Delay(150);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        TracksListView?.ScrollToItem(m.Track, ScrollToPosition.Center, false);
                    }
                    catch { }
                });
            });
        });
    }

    // --- Search hosts ---

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPlaylist))
            MainThread.BeginInvokeOnMainThread(UpdateSearchPlaceholders);
    }

    private void UpdateSearchPlaceholders()
    {
        string name = _viewModel.SelectedPlaylist?.Name?.Trim() ?? string.Empty;
        _mainSearchHost.Placeholder = name.Length == 0 ? "Search tracks" : $"Search {name}";
        MainSearchBar.Placeholder = _mainSearchHost.Placeholder;
    }

    private void UpdateSearchHeights()
    {
        // The inner content grid is SafeAreaEdges="Container", so MAUI keeps
        // the list area clear of both strips itself - exactly as it did
        // before the pane rework. The page-level None only exempts the ROOT
        // grid's direct children (the pane + full-window overlays); the
        // content grid opts back in, so NO manual inset arithmetic belongs
        // here: subtracting again would double-count the strips (the
        // round-6 "everything misaligned" complaint).
        double available = Height > 0 ? Height - 56 : 640;
        _mainSearchHost.SetAvailableHeight(available);
        _sidebarSearchHost.SetAvailableHeight(available);

        // The mini pane is 72dp of card that now sits ABOVE the gesture strip
        // (PlayerRoot margin model, see the pane) - the Container-padded
        // content grid already reserves the strip, so the list spacer only
        // needs the 72dp card itself.
        if (Math.Abs(MiniPlayerSpacer.HeightRequest - 72) > 0.5)
            MiniPlayerSpacer.HeightRequest = 72;
    }

    private void OnSearchHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchHostViewModel.IsOpen)) return;
        if (sender is not SearchHostViewModel host || !host.IsOpen) return;

        // Only one results menu can be open at a time.
        if (host == _mainSearchHost)
            _sidebarSearchHost.CloseCommand.Execute(null);
        else
            _mainSearchHost.CloseCommand.Execute(null);
    }

    private void OnMainSearchFocused(object? sender, EventArgs e)
    {
        _sidebarSearchHost.CloseCommand.Execute(null);
        _mainSearchHost.NotifyFocused();
    }

    private void OnSidebarSearchFocused(object? sender, EventArgs e)
    {
        _mainSearchHost.CloseCommand.Execute(null);
        _sidebarSearchHost.NotifyFocused();
    }

    private async void OnSearchResultActivated(object? sender, SearchResultItem item)
    {
        switch (item.Payload)
        {
            case Playlist playlist:
                _viewModel.SelectedPlaylist = playlist;
                if (_isSidebarOpen) await CloseSidebarInternalAsync();
                break;

            case PlaylistItem track:
                if (track.Playlist != null && track.Playlist != _viewModel.SelectedPlaylist)
                    _viewModel.SelectedPlaylist = track.Playlist;

                _viewModel.CurrentTrack = track;
                if (_isSidebarOpen) await CloseSidebarInternalAsync();
                break;

            case ISetting setting:
                if (_isSidebarOpen) await CloseSidebarInternalAsync();
                await _viewModel.OpenSettingAsync(setting);
                break;
        }
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
                _mainSearchHost.CloseCommand.Execute(null);
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

    private void OnHamburgerTapped(object? sender, TappedEventArgs e)
    {
        _mainSearchHost.CloseCommand.Execute(null);
        _ = OpenSidebarInternalAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // The library restores asynchronously, so the name in the placeholder
        // may only be known after the page was first constructed.
        UpdateSearchPlaceholders();
    }

    private async Task OpenSidebarInternalAsync(double velocityX = 0)
    {
        // Opening the drawer must not leave a hidden search entry focused with
        // the keyboard up; the user types in the drawer's own bar.
        MainSearchBar.Blur();

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
        _sidebarSearchHost.CloseCommand.Execute(null);
        SidebarSearchBar.Blur();

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
        if (_mainSearchHost.IsOpen)
        {
            _mainSearchHost.CloseCommand.Execute(null);
            return true;
        }

        if (_sidebarSearchHost.IsOpen)
        {
            _sidebarSearchHost.CloseCommand.Execute(null);
            return true;
        }

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
                if (vm.IsPlaylistSelectionMode)
                {
                    vm.TogglePlaylistSelection(playlist);
                    return;
                }
                vm.SelectedPlaylist = playlist;
            }
            await CloseSidebarInternalAsync();
        }
    }

    private void OnPlaylistIconTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
                vm.TogglePlaylistSelection(playlist);
        }
    }

    private void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                if (vm.IsTrackSelectionMode)
                {
                    vm.ToggleItemSelection(track);
                    return;
                }
                vm.CurrentTrack = track;
            }
        }
    }

    private void OnTrackIconTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
                vm.ToggleItemSelection(track);
        }
    }

    private void OnPlaySelectedClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
            vm.PlaySelectedCommand.Execute(null);
    }

    private void OnShuffleSelectedClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
            vm.ShuffleSelectedCommand.Execute(null);
    }

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
            vm.ToggleSelectAllTracksCommand.Execute(null);
    }

    private void OnSelectAllPlaylistsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
            vm.ToggleSelectAllPlaylistsCommand.Execute(null);
    }

    private void OnCancelSelectionClicked(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
            vm.CancelSelectionCommand.Execute(null);
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

    private void OnListScrolled(object? sender, ScrolledEventArgs e)
    {
        // Ignore scroll events from the sidebar's playlist list
        if (sender != TracksListView) return;

        _lastScrollY = e.ScrollY;
        UpdateStickyToolbar();
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