using Musicthingy.Models;

namespace Musicthingy.Views;

public partial class MobileMainPage : FlyoutPage
{
    private SwipeView? _openedSwipeView;

    public MobileMainPage()
    {
        InitializeComponent();
        BindingContext = new ViewModels.MainViewModel();
    }

    private void OnPlaylistTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();

        if (sender is BindableObject bindable && bindable.BindingContext is Playlist playlist)
        {
            if (BindingContext is ViewModels.MainViewModel vm)
            {
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

    // Standard EventHandler works since VirtualizeListView inherits MAUI's ScrollView events
    private void OnListScrolled(object sender, ScrolledEventArgs e)
    {
        CloseSwipeView();
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