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
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                _isProgrammaticScroll = true;

                // Give the UI thread a generous moment to tear down the old list 
                // and render the new one if the playlist just switched.
                await Task.Delay(300);

                var vm = BindingContext as ViewModels.MainViewModel;
                if (vm?.SelectedPlaylist == null)
                {
                    _isProgrammaticScroll = false;
                    return;
                }

                var items = vm.SelectedPlaylist.Items;
                int targetIndex = items.IndexOf(m.Track);

                if (targetIndex != -1)
                {
                    // Calculate our approximate current index using the known height of your Track rows (approx 62px)
                    int currentIndex = (int)(Math.Max(0, _lastScrollY) / 62.0);

                    // CRASH PREVENTION ALGORITHM:
                    // If the distance is > 40 items, we act as an "elevator", chunking the jump into smaller blocks.
                    // This prevents MAUI from inflating hundreds of SwipeViews synchronously and crashing the app.
                    if (Math.Abs(targetIndex - currentIndex) > 40)
                    {
                        int step = targetIndex > currentIndex ? 40 : -40;
                        int i = currentIndex + step;

                        while ((step > 0 && i < targetIndex) || (step < 0 && i > targetIndex))
                        {
                            if (i >= 0 && i < items.Count)
                            {
                                var intermediateTrack = items[i];
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    try { TracksListView?.ScrollToItem(intermediateTrack, ScrollToPosition.Center, false); }
                                    catch { }
                                });

                                // Yield to the Garbage Collector & UI Thread so it doesn't freeze
                                await Task.Delay(25);
                            }
                            i += step;
                        }
                    }

                    // The final precise jump to the target
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try { TracksListView?.ScrollToItem(m.Track, ScrollToPosition.Center, false); }
                        catch { }
                    });
                }

                // Wait for the final layout to settle before re-enabling manual scroll detection
                await Task.Delay(300);
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