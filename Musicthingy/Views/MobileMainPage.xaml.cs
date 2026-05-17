using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using MPowerKit.VirtualizeListView;
using Microsoft.Maui.Controls;

namespace Musicthingy.Views;

public partial class MobileMainPage : ContentPage
{
    private bool _isProgrammaticScroll = false;
    private double _lastScrollY = 0;
    private CancellationTokenSource? _scrollCts;

    // Custom Sidebar Panning variables
    private double _panStartX = 0;
    private bool _isSidebarPanning = false;
    private readonly double _sidebarWidth = 360;

    public MobileMainPage()
    {
        InitializeComponent();
        BindingContext = new ViewModels.MainViewModel();

        Services.SheetManager.Host = GlobalSheetHost;

        // Listen for requests to scroll to the playing track
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
            catch (TaskCanceledException)
            {
            }
        });
    }

    // --- Custom Sidebar Pan & Interaction Logic ---

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
                break;

            case GestureStatus.Running:
                if (!_isSidebarPanning) return;

                double newTx = _panStartX + e.TotalX;

                if (newTx > 0) newTx = 0;
                if (newTx < -_sidebarWidth) newTx = -_sidebarWidth;

                SidebarGrid.TranslationX = newTx;
                SidebarDarken.Opacity = (newTx + _sidebarWidth) / _sidebarWidth;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isSidebarPanning = false;

                if (SidebarGrid.TranslationX > -(_sidebarWidth * 0.5))
                {
                    _ = OpenSidebarInternalAsync();
                }
                else
                {
                    _ = CloseSidebarInternalAsync();
                }
                break;
        }
    }

    private async Task OpenSidebarInternalAsync()
    {
        SidebarDarken.IsVisible = true;
        EdgeSwipeZone.IsVisible = false;
        _ = SidebarDarken.FadeToAsync(1, 250, Easing.CubicOut);
        await SidebarGrid.TranslateToAsync(0, 0, 250, Easing.CubicOut);
    }

    private async Task CloseSidebarInternalAsync()
    {
        _ = SidebarDarken.FadeToAsync(0, 250, Easing.CubicIn);
        await SidebarGrid.TranslateToAsync(-_sidebarWidth, 0, 250, Easing.CubicIn);
        SidebarDarken.IsVisible = false;
        EdgeSwipeZone.IsVisible = true;
    }

    private void OnHamburgerTapped(object? sender, TappedEventArgs e)
    {
        _ = OpenSidebarInternalAsync();
    }

    private void OnSidebarOverlayTapped(object? sender, TappedEventArgs e)
    {
        _ = CloseSidebarInternalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (SidebarDarken.IsVisible)
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
    }

    /// <summary>
    /// Event handler to open the Bottom Sheet Context Menu for a track.
    /// </summary>
    private async void OnTrackOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                ContextMenuSheet menuSheet = new ContextMenuSheet
                {
                    HasHeader = true,
                    HeaderTitle = track.Title,
                    HeaderSubtitle = vm.SelectedPlaylist?.Name ?? "Track",
                    HeaderImageSource = track.ItemIconSource
                };

                menuSheet.MenuItems = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Title = "Edit Track",
                    IconSource = "pencil_icon.png",
                    Action = async () =>
                    {
                        await Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.EditTrackCommand.Execute(track);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Delete from Playlist",
                    IconSource = "trash_2_icon.png",
                    IsDestructive = true,
                    Action = async () =>
                    {
                        await Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.DeleteTrackCommand.Execute(track);
                    }
                }
            };

                if (Services.SheetManager.Host != null)
                {
                    await Services.SheetManager.Host.ShowAsync(menuSheet);
                }
            }
        }
    }

    /// <summary>
    /// Event handler to open the Bottom Sheet Context Menu for a playlist.
    /// </summary>
    private async void OnPlaylistOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                ContextMenuSheet menuSheet = new ContextMenuSheet
                {
                    HasHeader = true,
                    HeaderTitle = playlist.Name,
                    HeaderSubtitle = $"{playlist.ItemCount} tracks • {playlist.DisplayDuration}",
                    HeaderImageSource = playlist.IconImageSource
                };

                menuSheet.MenuItems = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Title = "Delete Playlist",
                    IconSource = "trash_2_icon.png",
                    IsDestructive = true,
                    Action = async () =>
                    {
                        await Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.DeletePlaylistCommand.Execute(playlist);
                    }
                }
            };

                if (Services.SheetManager.Host != null)
                {
                    await Services.SheetManager.Host.ShowAsync(menuSheet);
                }
            }
        }
    }
}