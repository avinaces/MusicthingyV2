using Microsoft.Maui.Graphics;
using Musicthingy.Models;
using Musicthingy.Services;
using Musicthingy.Settings;
using Musicthingy.Settings.Definitions;
using Musicthingy.ViewModels;

namespace Musicthingy.Views;

public partial class SettingsCategoryPage : ContentPage
{
    private readonly SettingsManager _settingsManager;
    private readonly TrackCacheService? _trackCacheService;
    private readonly MainViewModel? _mainViewModel;

    public SettingsCategoryPage(SettingsManager settingsManager, SettingCategory category, ISetting? focusSetting = null)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _settingsManager = settingsManager;

        // Safely resolve services from DI
        IServiceProvider? services = Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext?.Services;
        _trackCacheService = services?.GetService<TrackCacheService>();
        _mainViewModel = services?.GetService<MainViewModel>();

        TitleLabel.Text = category.ToString();
        SettingsList.ItemsSource = settingsManager.GetVisibleSettings().Where(s => s.Category == category).ToList();

        if (focusSetting != null)
            _ = ScrollToSettingAsync(focusSetting);

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
        else if (category == SettingCategory.Playback)
        {
            PlaybackDiagnosticsContainer.IsVisible = true;
            UpdatePlaybackDiagnostics();

            if (_mainViewModel != null)
                _mainViewModel.DiagnosticsChanged += OnDiagnosticsChanged;
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

    // ─────────────────────────────────────────────────────────────────────
    //  Playback diagnostics panel
    // ─────────────────────────────────────────────────────────────────────

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdatePlaybackDiagnostics);
    }

    private void UpdatePlaybackDiagnostics()
    {
        PlaybackDiagnostics diag = _mainViewModel?.GetPlaybackDiagnostics() ?? PlaybackDiagnostics.Empty;

        DiagDetailsContainer.IsVisible = diag.HasTrack;
        DiagEmptyLabel.IsVisible = !diag.HasTrack;

        if (!diag.HasTrack)
        {
            DiagTrackTitleLabel.Text = "Nothing playing";
            DiagTrackSubtitleLabel.Text = "Technical playback details";
            DiagCacheBadge.IsVisible = false;
            SetStatus("IDLE", ThemeResources.Color("MutedColor"), ThemeResources.Color("SurfaceRaised"));
            return;
        }

        DiagTrackTitleLabel.Text = diag.Title;

        DiagTrackSubtitleLabel.Text = string.IsNullOrWhiteSpace(diag.PlaylistName) ? "Now playing" : diag.PlaylistName;
        DiagCacheBadge.IsVisible = diag.IsCached;

        if (diag.IsBuffering)
            SetStatus(diag.StreamType == null ? "LOADING" : "BUFFERING",
                ThemeResources.Color("AccentColor"), ThemeResources.Color("AccentContainer"));
        else if (diag.IsPlaying)
            SetStatus("PLAYING", ThemeResources.Color("AccentColor"), ThemeResources.Color("AccentContainer"));
        else
            SetStatus("PAUSED", ThemeResources.Color("MutedColor"), ThemeResources.Color("SurfaceRaised"));

        DiagFormatValue.Text = FormatFormat(diag);
        DiagStreamValue.Text = ValueOrDash(diag.StreamType);
        DiagClientValue.Text = ValueOrDash(diag.Client);
        DiagSampleRateValue.Text = diag.SampleRateHz is > 0
            ? $"{diag.SampleRateHz.Value / 1000.0:0.#} kHz"
            : "—";
        DiagBitrateValue.Text = diag.BitrateKbps is > 0
            ? $"{diag.BitrateKbps} kbps"
            : "—";
        DiagItagValue.Text = diag.Itag?.ToString() ?? "—";

        double duration = diag.DurationMs;
        double position = duration > 0 ? Math.Min(diag.PositionMs, duration) : diag.PositionMs;
        DiagPositionLabel.Text = $"{FormatTime(position)} / {(duration > 0 ? FormatTime(duration) : "—")}";

        double buffered = duration > 0 ? Math.Min(diag.BufferedMs, duration) : diag.BufferedMs;
        double bufferProgress = duration > 0 ? Math.Clamp(buffered / duration, 0, 1) : 0;
        DiagBufferBar.Progress = bufferProgress;
        DiagBufferPercentLabel.Text = duration > 0 ? $"{bufferProgress * 100:F0}%" : "—";
        DiagBufferLabel.Text = diag.BufferedMs > 0 ? $"Buffered {FormatTime(buffered)}" : "Buffered —";

        var details = new List<string>();
        if (diag.SabrBufferedBytes is > 0)
            details.Add($"SABR buffer {FormatBytes(diag.SabrBufferedBytes.Value)}");
        if (diag.FileSizeBytes is > 0)
            details.Add(FormatBytes(diag.FileSizeBytes.Value));
        if (diag.ResolveMs > 0)
            details.Add($"Resolved in {diag.ResolveMs} ms");
        string? host = GetStreamHost(diag.StreamUrl);
        if (!string.IsNullOrEmpty(host))
            details.Add(diag.IsCached ? "Cache" : host);
        DiagDetailLabel.Text = details.Count > 0 ? string.Join("   ·   ", details) : string.Empty;
    }

    private void SetStatus(string text, Color textColor, Color backgroundColor)
    {
        DiagStatusLabel.Text = text;
        DiagStatusLabel.TextColor = textColor;
        DiagStatusBorder.BackgroundColor = backgroundColor;
    }

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatFormat(PlaybackDiagnostics diag)
    {
        string? codec = PrettyCodec(diag.Codec);
        string? container = string.IsNullOrWhiteSpace(diag.Container)
            ? null
            : diag.Container.Trim().ToUpperInvariant();

        if (codec != null && container != null) return $"{codec} · {container}";
        if (codec != null) return codec;
        if (container != null) return container;
        return "—";
    }

    private static string? PrettyCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;

        string value = codec.Trim();
        if (value.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (value.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (value.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        if (value.Equals("flac", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (value.Equals("alac", StringComparison.OrdinalIgnoreCase)) return "ALAC";
        if (value.Equals("pcm", StringComparison.OrdinalIgnoreCase)) return "PCM";
        if (value.Equals("mp3", StringComparison.OrdinalIgnoreCase)) return "MP3";
        return value.ToUpperInvariant();
    }

    private static string? GetStreamHost(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("sabr://", StringComparison.OrdinalIgnoreCase)) return "SABR session";
        if (url.StartsWith("/", StringComparison.Ordinal) ||
            url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            return "Local file";

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }

    private static string FormatTime(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
            milliseconds = 0;

        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
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

    private async Task ScrollToSettingAsync(ISetting setting)
    {
        // Give the CollectionView one layout pass before scrolling.
        await Task.Delay(250);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                SettingsList.ScrollTo(setting, position: ScrollToPosition.Center, animate: false);
            }
            catch
            {
                // The row may not be realised yet; the category is still open.
            }
        });
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        // Unsubscribe to prevent memory leaks
        if (_trackCacheService != null)
        {
            _trackCacheService.CacheUpdated -= OnCacheUpdated;
            _trackCacheService.DownloadProgressChanged -= OnDownloadProgressChanged;
        }

        if (_mainViewModel != null)
            _mainViewModel.DiagnosticsChanged -= OnDiagnosticsChanged;

        base.OnNavigatedFrom(args);
    }
}
