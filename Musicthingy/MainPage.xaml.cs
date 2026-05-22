using Musicthingy.ViewModels;

namespace Musicthingy;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();

        // This tells the XAML where to look for its data
        BindingContext = viewModel;
    }

    private void HorizontalStackLayout_ChildRemoved(object sender, ElementEventArgs e)
    {

    }
}