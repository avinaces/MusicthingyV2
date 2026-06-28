using Musicthingy.Services;
using Musicthingy.Settings;

namespace Musicthingy.Views;

public partial class SettingsCategoryPage : ContentPage
{
    public SettingsCategoryPage(SettingsManager settingsManager, SettingCategory category)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        TitleLabel.Text = category.ToString();

        SettingsList.ItemsSource = settingsManager.GetVisibleSettings()
            .Where(s => s.Category == category)
            .ToList();
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopModalAsync();
}