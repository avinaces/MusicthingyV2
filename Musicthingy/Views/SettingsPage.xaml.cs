using Musicthingy.Services;
using Musicthingy.Settings;

namespace Musicthingy.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsManager _settingsManager;

    // Small UI model to hold the icon + category data for the list
    public class CategoryItem
    {
        public string Title { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public SettingCategory Category { get; set; }
    }

    public SettingsPage(SettingsManager settingsManager)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _settingsManager = settingsManager;

        // Build list with mapped icons
        CategoriesList.ItemsSource = Enum.GetValues(typeof(SettingCategory))
            .Cast<SettingCategory>()
            .Where(c => c != SettingCategory.Hidden)
            .Select(c => new CategoryItem
            {
                Title = c.ToString(),
                Category = c,
                Icon = GetCategoryIcon(c)
            })
            .ToList();
    }

    private string GetCategoryIcon(SettingCategory category) => category switch
    {
        SettingCategory.Appearance => "palette_icon.png",
        SettingCategory.Playback => "circle_play_icon.png",
        SettingCategory.General => "settings_icon.png",
        SettingCategory.Storage => "database_icon.png",
        _ => "settings_icon.png"
    };

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (Navigation.ModalStack.Count > 0)
            await Navigation.PopModalAsync();
        else
            await Navigation.PopAsync();
    }

    private async void OnCategoryTapped(object sender, TappedEventArgs e)
    {
        // Extract the typed CategoryItem we built in the constructor
        if (e.Parameter is CategoryItem item)
        {
            await Navigation.PushModalAsync(new SettingsCategoryPage(_settingsManager, item.Category));
        }
    }
}