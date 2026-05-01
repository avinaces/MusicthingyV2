using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Threading;

namespace Musicthingy.Models;

public partial class Playlist : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("name")]
    public partial string Name { get; set; } = string.Empty; [ObservableProperty]
    [JsonPropertyName("icon")]
    public partial string IconUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("createdAt")]
    public partial DateTime CreatedAt { get; set; }

    [ObservableProperty]
    [JsonPropertyName("updatedAt")]
    public partial DateTime UpdatedAt { get; set; }

    [ObservableProperty]
    [JsonPropertyName("items")]
    public partial ObservableCollection<PlaylistItem> Items { get; set; } = new(); [ObservableProperty]
    [JsonPropertyName("iconType")]
    public partial string IconType { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("source")]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("tags")]
    public partial ObservableCollection<string> Tags { get; set; } = new();

    [ObservableProperty]
    public partial bool IsHovered { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void OnPointerEntered() => IsHovered = true;

    [RelayCommand]
    private void OnPointerExited() => IsHovered = false;

    [JsonIgnore]
    public int ItemCount => Items.Count;

    // Static cache and throttler across ALL playlists
    private static readonly ConcurrentDictionary<string, ImageSource> _sharedPlaylistIconCache = new();
    private static readonly SemaphoreSlim _playlistIconSemaphore = new(4, 4);

    private ImageSource? _iconImageSource;
    private bool _isGeneratingIcon;

    [JsonIgnore]
    public ImageSource? IconImageSource
    {
        get
        {
            if (_iconImageSource != null) return _iconImageSource;

            // Fast path: if we've generated this icon before, return it instantly to the UI
            string cacheKey = string.IsNullOrEmpty(IconUrl) ? $"NAME_{Name}" : IconUrl;
            if (_sharedPlaylistIconCache.TryGetValue(cacheKey, out var cachedSource))
            {
                _iconImageSource = cachedSource;
                return cachedSource;
            }

            if (!_isGeneratingIcon)
            {
                _isGeneratingIcon = true;
                GenerateIconAsync(cacheKey);
            }

            return null;
        }
    }

    private void GenerateIconAsync(string cacheKey)
    {
        Task.Run(async () =>
        {
            try
            {
                // Prevent ThreadPool explosion by limiting concurrent operations
                await _playlistIconSemaphore.WaitAsync();

                // Double-check cache in case another task just finished it
                if (_sharedPlaylistIconCache.TryGetValue(cacheKey, out var cachedSource))
                {
                    _iconImageSource = cachedSource;
                    MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(IconImageSource)));
                    return;
                }

                const int playlistIconUiSize = 110;
                const int playlistIconLogicalSize = 12;
                ImageSource? source = null;

                if (string.IsNullOrEmpty(IconUrl))
                {
                    source = IconGenerator.GenerateIcon(Name, playlistIconLogicalSize, playlistIconUiSize);
                }
                else if (IconUrl.StartsWith("data:image"))
                {
                    var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                    var imageBytes = Convert.FromBase64String(base64Data);
                    source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, playlistIconUiSize)
                             ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                }
                else if (File.Exists(IconUrl))
                {
                    var imageBytes = await File.ReadAllBytesAsync(IconUrl);
                    source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, playlistIconUiSize)
                             ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                }
                else
                {
                    source = ImageSource.FromFile(IconUrl);
                }

                if (source != null)
                {
                    _sharedPlaylistIconCache[cacheKey] = source;
                    _iconImageSource = source;
                    MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(IconImageSource)));
                }
            }
            catch
            {
                // Ignore silent failures to prevent alert spam during scrolling
            }
            finally
            {
                _playlistIconSemaphore.Release();
            }
        });
    }

    partial void OnIconUrlChanged(string value)
    {
        _iconImageSource = null;
        _isGeneratingIcon = false;
        OnPropertyChanged(nameof(IconImageSource));
    }
}