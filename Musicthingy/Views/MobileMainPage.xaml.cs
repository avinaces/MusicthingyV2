using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using MPowerKit.VirtualizeListView;

namespace Musicthingy.Views;

public partial class MobileMainPage : FlyoutPage
{
    private SwipeView? _openedSwipeView;
    private bool _isProgrammaticScroll = false;
    private double _lastScrollY = 0;

    public MobileMainPage()
    {
        InitializeComponent();
        BindingContext = new ViewModels.MainViewModel();

        // Listen for requests to scroll to the playing track
        WeakReferenceMessenger.Default.Register<ScrollToTrackMessage>(this, (r, m) =>
        {
            Task.Run(async () =>
            {
                _isProgrammaticScroll = true;

                // Wait a fraction of a second for the list to render if the playlist just switched
                await Task.Delay(150);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        TracksListView?.ScrollToItem(m.Track, ScrollToPosition.Center, false);
                    }
                    catch { }
                });

                // Since the jump is now instant, we only need a tiny delay 
                // to ignore the immediate scroll events fired by the jump.
                await Task.Delay(200);
                _isProgrammaticScroll = false;
            });
        });
    }

    private void OnPlaylistTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();

        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
                vm.IsAutoFollowEnabled = false; // Disable on manual playlist change
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
                vm.IsAutoFollowEnabled = false; // Disable if user manually selects a track to play
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
            return;
        }

        // Only disable Auto-Follow if it's a real deliberate vertical movement > 5 pixels
        // (This filters out random MAUI layout shifts so it doesn't accidentally turn off)
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