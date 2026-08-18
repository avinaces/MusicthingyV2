using Musicthingy.Services;
using Musicthingy.Settings;
using Musicthingy.Settings.Definitions;

namespace Musicthingy.Views;

public partial class SettingsCategoryPage : ContentPage
{
    private readonly SettingsManager _settingsManager;
    private readonly TrackCacheService? _trackCacheService;

    public SettingsCategoryPage(SettingsManager settingsManager, SettingCategory category)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _settingsManager = settingsManager;

        // Safely resolve the cache service from DI
        _trackCacheService = Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext?.Services.GetService<TrackCacheService>();

        TitleLabel.Text = category.ToString();
        SettingsList.ItemsSource = settingsManager.GetVisibleSettings().Where(s => s.Category == category).ToList();

        if (category == SettingCategory.Storage)
        {
            StorageStatsContainer.IsVisible = true;
            UpdateStorageStats();
        }
    }

    private void UpdateStorageStats()
    {
        if (_trackCacheService == null) return;

        double usedMb = _trackCacheService.GetTotalCacheSizeBytes() / 1048576.0;
        double maxMb = _settingsManager.GetVisibleSettings().OfType<MaxCacheSizeSetting>().FirstOrDefault()?.Value ?? 1024.0;

        UsedStorageLabel.Text = $"Used: {usedMb:F1} MB";
        MaxStorageLabel.Text = $"Limit: {maxMb:F0} MB";
        StorageProgressBar.Progress = Math.Min(1.0, usedMb / maxMb);
    }

    private async void OnClearCacheClicked(object sender, EventArgs e)
    {
        if (_trackCacheService != null)
        {
            await _trackCacheService.ClearCacheAsync();
            UpdateStorageStats();
        }
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopModalAsync();
}