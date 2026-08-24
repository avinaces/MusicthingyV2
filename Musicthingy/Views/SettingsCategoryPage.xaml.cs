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

            // Subscribe to cache updates + download progress
            if (_trackCacheService != null)
            {
                _trackCacheService.CacheUpdated += OnCacheUpdated;
                _trackCacheService.DownloadProgressChanged += OnDownloadProgressChanged;
            }
        }
    }

    private void OnCacheUpdated(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateStorageStats);
    }

    private void OnDownloadProgressChanged(TrackCacheService.DownloadProgress progress)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Only show the container when there's active progress (downloaded > 0 and total not yet reached)
            bool isComplete = progress.TotalBytes.HasValue && progress.DownloadedBytes >= progress.TotalBytes.Value;
            bool isActive = progress.DownloadedBytes > 0 && !isComplete;

            DownloadProgressContainer.IsVisible = isActive || isComplete;

            if (isActive)
            {
                // Extract video ID from the URL for display
                string displayUrl = progress.Url;
                var videoIdMatch = System.Text.RegularExpressions.Regex.Match(progress.Url, @"(?:v=|youtu\.be/)([a-zA-Z0-9_-]{11})");
                if (videoIdMatch.Success)
                    displayUrl = videoIdMatch.Groups[1].Value;

                DownloadUrlLabel.Text = displayUrl;
                DownloadSpeedLabel.Text = FormatSpeed(progress.SpeedKBps);

                if (progress.TotalBytes.HasValue)
                {
                    double pct = (double)progress.DownloadedBytes / progress.TotalBytes.Value;
                    DownloadProgressBar.Progress = Math.Min(1.0, pct);
                    DownloadSizeLabel.Text = $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes.Value)} ({pct * 100:F0}%)";
                }
                else
                {
                    DownloadProgressBar.Progress = 0; // indeterminate — no total known
                    DownloadSizeLabel.Text = $"{FormatBytes(progress.DownloadedBytes)} downloaded (size unknown)";
                }
            }
            else if (isComplete)
            {
                // Brief "done" flash
                DownloadProgressBar.Progress = 1.0;
                DownloadSizeLabel.Text = $"Done — {FormatBytes(progress.DownloadedBytes)}";
                DownloadSpeedLabel.Text = "";

                // Hide after 1.5 seconds
                Task.Delay(1500).ContinueWith(_ =>
                    MainThread.BeginInvokeOnMainThread(() => DownloadProgressContainer.IsVisible = false));
            }
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1048576.0:F1} MB";
    }

    private static string FormatSpeed(double speedKBps)
    {
        if (speedKBps < 1024) return $"{speedKBps:F0} KB/s";
        return $"{speedKBps / 1024.0:F1} MB/s";
    }

    private void UpdateStorageStats()
    {
        if (_trackCacheService == null) return;

        double usedMb = _trackCacheService.GetTotalCacheSizeBytes() / 1048576.0;
        double maxMb = _settingsManager.GetVisibleSettings().OfType<MaxCacheSizeSetting>().FirstOrDefault()?.Value ?? 1024.0;

        UsedStorageLabel.Text = $"Used: {usedMb:F1} MB";

        if (maxMb == 0)
        {
            MaxStorageLabel.Text = "Limit: Unlimited";
            StorageProgressBar.Progress = 0;
        }
        else
        {
            MaxStorageLabel.Text = $"Limit: {maxMb:F0} MB";
            StorageProgressBar.Progress = Math.Min(1.0, usedMb / maxMb);
        }
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

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        // Unsubscribe to prevent memory leaks
        if (_trackCacheService != null)
        {
            _trackCacheService.CacheUpdated -= OnCacheUpdated;
            _trackCacheService.DownloadProgressChanged -= OnDownloadProgressChanged;
        }
        base.OnNavigatedFrom(args);
    }
}