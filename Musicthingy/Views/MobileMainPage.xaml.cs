namespace Musicthingy.Views;

public partial class MobileMainPage : FlyoutPage
{
    // Added nullable '?' to fix CS8618 and CS8625 warnings
    private SwipeView? _openedSwipeView;

    public MobileMainPage()
    {
        InitializeComponent();
        BindingContext = new ViewModels.MainViewModel();
    }

    private void OnPlaylistSelected(object sender, SelectionChangedEventArgs e)
    {
        CloseSwipeView();

        if (e.CurrentSelection.FirstOrDefault() != null)
        {
            this.IsPresented = false;
        }
    }

    private void OnTrackSelected(object sender, SelectionChangedEventArgs e)
    {
        CloseSwipeView();
    }

    private void OnSwipeStarted(object sender, SwipeStartedEventArgs e)
    {
        // Safe type check to fix CS8601 warning
        if (sender is SwipeView currentSwipeView)
        {
            // If a DIFFERENT swipe view is already open, snap it shut
            if (_openedSwipeView != null && _openedSwipeView != currentSwipeView)
            {
                _openedSwipeView.Close();
            }

            // Track the currently open one
            _openedSwipeView = currentSwipeView;
        }
    }

    private void OnListScrolled(object sender, ItemsViewScrolledEventArgs e)
    {
        // Snap the swipe view shut the moment the user starts scrolling
        CloseSwipeView();
    }

    private void CloseSwipeView()
    {
        if (_openedSwipeView != null)
        {
            _openedSwipeView.Close(); // Triggers the native snap-back animation
            _openedSwipeView = null;
        }
    }

    private void OnDeletePlaylistTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();

        // IMPORTANT: The modal exists in the Detail view. 
        // We MUST close the drawer instantly so the user can see the modal popup.
        this.IsPresented = false;
    }

    private void OnTrackActionTapped(object sender, TappedEventArgs e)
    {
        CloseSwipeView();
    }
}