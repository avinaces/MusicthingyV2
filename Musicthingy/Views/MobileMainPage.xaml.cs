using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using MPowerKit.VirtualizeListView;

namespace Musicthingy.Views;

public partial class MobileMainPage : FlyoutPage
{
    private SwipeView? _openedSwipeView;
    private bool _isProgrammaticScroll = false;
    private double _lastScrollY = 0;

    private CancellationTokenSource? _scrollCts;

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

                // Allow UI to render if playlist changed
                await Task.Delay(150);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        TracksListView?.ScrollToItem(m.Track, ScrollToPosition.Center, false);
                    }
                    catch { }
                });

                // Instead of fixed delay, wait for scroll to settle
                ResetScrollDetection();
            });
        });
    }

    private void ResetScrollDetection()
    {
        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();

        var token = _scrollCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // Wait until no scroll events fire for 300ms
                await Task.Delay(300, token);
                _isProgrammaticScroll = false;
            }
            catch (TaskCanceledException)
            {
                // Expected when new scroll events occur
            }
        });
    }

    private void OnPlaylistTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();

        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
                vm.SelectedPlaylist = playlist;
            }
            this.IsPresented = false;
        }
    }

    private void OnTrackTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();

        if (sender is BindableObject bindable && bindable.BindingContext is PlaylistItem track)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
                vm.CurrentTrack = track;
            }
        }
    }

    private void OnSwipeStarted(object sender, SwipeStartedEventArgs e)
    {
        if (sender is SwipeView currentSwipeView)
        {
            if (_openedSwipeView != null && _openedSwipeView != currentSwipeView)
            {
                _openedSwipeView.Close();
            }
            _openedSwipeView = currentSwipeView;
        }
    }

    private void OnListScrolled(object sender, ScrolledEventArgs e)
    {
        CloseSwipeView();

        if (_isProgrammaticScroll)
        {
            _lastScrollY = e.ScrollY;

            // Extend debounce window while scrolling continues
            ResetScrollDetection();
            return;
        }

        // Only disable Auto-Follow on real user movement
        if (Math.Abs(e.ScrollY - _lastScrollY) > 5)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false;
            }
        }

        _lastScrollY = e.ScrollY;
    }

    private void CloseSwipeView()
    {
        if (_openedSwipeView != null)
        {
            _openedSwipeView.Close();
            _openedSwipeView = null;
        }
    }

    private void OnDeletePlaylistTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();
        this.IsPresented = false;
    }

    private void OnTrackActionTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();
    }
}