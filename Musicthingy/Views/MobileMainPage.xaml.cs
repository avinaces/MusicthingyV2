using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using MPowerKit.VirtualizeListView;
using Microsoft.Maui.Controls;
using Musicthingy.ViewModels;

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

    public MobileMainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

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


    private double _sidebarVelocityX = 0;
    private double _sidebarLastPanX = 0;
    private DateTime _sidebarLastPanTime;

    private void HandleSidebarPan(PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isSidebarPanning = true;
                SidebarDarken.IsVisible = true;
                EdgeSwipeZone.IsVisible = false;
                _panStartX = SidebarGrid.TranslationX;

                _sidebarVelocityX = 0;
                _sidebarLastPanX = 0;
                _sidebarLastPanTime = DateTime.UtcNow;
                break;

            case GestureStatus.Running:
                if (!_isSidebarPanning) return;

                var now = DateTime.UtcNow;
                var dt = (now - _sidebarLastPanTime).TotalMilliseconds;
                if (dt > 0)
                {
                    double cv = (e.TotalX - _sidebarLastPanX) / dt;
                    _sidebarVelocityX = (_sidebarVelocityX == 0) ? cv : (_sidebarVelocityX * 0.2 + cv * 0.8);
                }
                _sidebarLastPanX = e.TotalX;
                _sidebarLastPanTime = now;

                double newTx = _panStartX + e.TotalX;
                if (newTx > 0) newTx = 0;
                if (newTx < -_sidebarWidth) newTx = -_sidebarWidth;

                SidebarGrid.TranslationX = newTx;
                SidebarDarken.Opacity = (newTx + _sidebarWidth) / _sidebarWidth;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isSidebarPanning = false;

                if (SidebarGrid.TranslationX > -(_sidebarWidth * 0.5) || _sidebarVelocityX > 0.5)
                {
                    _ = OpenSidebarInternalAsync(_sidebarVelocityX);
                }
                else
                {
                    _ = CloseSidebarInternalAsync(_sidebarVelocityX);
                }
                break;
        }
    }

    private async Task OpenSidebarInternalAsync(double velocityX = 0)
    {
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
        uint duration = 250;
        double distance = Math.Abs(-_sidebarWidth - SidebarGrid.TranslationX);
        if (velocityX < -0.1) duration = (uint)Math.Clamp(distance / Math.Abs(velocityX), 100, 350);

        _ = SidebarDarken.FadeToAsync(0, duration, Easing.CubicIn);
        await SidebarGrid.TranslateToAsync(-_sidebarWidth, 0, duration, Easing.CubicIn);
        SidebarDarken.IsVisible = false;
        EdgeSwipeZone.IsVisible = true;
    }

    // Modify OnHamburgerTapped and OnSidebarOverlayTapped to pass '0' to velocity parameters:
    private void OnHamburgerTapped(object? sender, TappedEventArgs e) => _ = OpenSidebarInternalAsync(0);
    private void OnSidebarOverlayTapped(object? sender, TappedEventArgs e) => _ = CloseSidebarInternalAsync(0);

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
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
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
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.DeleteTrackCommand.Execute(track);
                    }
                }
            };

                if (Services.SheetManager.Host != null)
                {
                    _ = Services.SheetManager.Host.ShowAsync(menuSheet);
                }
            }
        }
    }

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
                        Title = "Edit Playlist",
                        IconSource = "pencil_icon.png",
                        Action = async () =>
                        {
                            _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                            vm.EditPlaylistCommand.Execute(playlist);
                        }
                    },
                    new ContextMenuItem
                    {
                        Title = "Export Playlist",
                        IconSource = "upload_icon.png",
                        Action = async () =>
                        {
                            _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                            vm.ExportPlaylistCommand.Execute(playlist);
                        }
                    },
                    new ContextMenuItem
                    {
                        Title = "Delete Playlist",
                        IconSource = "trash_2_icon.png",
                        IsDestructive = true,
                        Action = async () =>
                        {
                            _ = Services.SheetManager.Host!.HideAsync(menuSheet);
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