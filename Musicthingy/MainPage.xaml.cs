using Musicthingy.ViewModels;

namespace Musicthingy;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        // This tells the XAML where to look for its data
        BindingContext = new MainViewModel();
    }
}