using Musicthingy.Search;
using Musicthingy.Services;
using Musicthingy.Settings;
using Musicthingy.ViewModels;

namespace Musicthingy.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsManager _settingsManager;
    private readonly SearchHostViewModel _searchHost;

    // Small UI model to hold the icon + category data for the list
    public class CategoryItem
    {
        public string Title { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public SettingCategory Category { get; set; }
    }

    public SettingsPage(SettingsManager settingsManager, SearchService searchService)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _settingsManager = settingsManager;

        // Search from this page prioritises settings above the music library.
        _searchHost = new SearchHostViewModel(searchService, SearchOrigin.Settings)
        {
            Placeholder = "Search settings"
        };

        SettingsSearchBar.BindingContext = _searchHost;
        SettingsSearchResults.BindingContext = _searchHost;
        SettingsSearchBar.SetBinding(SearchBarView.QueryProperty, new Binding(nameof(SearchHostViewModel.Query), BindingMode.TwoWay, source: _searchHost));
        SettingsSearchBar.SetBinding(SearchBarView.PlaceholderProperty, new Binding(nameof(SearchHostViewModel.Placeholder), source: _searchHost));

        _searchHost.ResultActivated += OnSearchResultActivated;
        _searchHost.Dismissed += (_, _) => SettingsSearchBar.Blur();
        SettingsSearchBar.SearchFocused += (_, _) => _searchHost.NotifyFocused();

        SizeChanged += (_, _) => _searchHost.SetAvailableHeight(Height > 0 ? Height - 56 : 640);

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

    protected override bool OnBackButtonPressed()
    {
        if (_searchHost.IsOpen)
        {
            _searchHost.CloseCommand.Execute(null);
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private async void OnSearchResultActivated(object? sender, SearchResultItem item)
    {
        if (item.Payload is not ISetting setting) return;

        await Navigation.PushModalAsync(
            new SettingsCategoryPage(_settingsManager, setting.Category, setting));
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (_searchHost.IsOpen)
        {
            _searchHost.CloseCommand.Execute(null);
            return;
        }

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
