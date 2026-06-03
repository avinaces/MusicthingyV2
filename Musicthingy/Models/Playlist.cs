using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;

namespace Musicthingy.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaylistIconType
{
    Playlist,
    Track,
    YouTube
}

public partial class Playlist : ObservableObject
{

    public Playlist()
    {
        Items.CollectionChanged += Items_CollectionChanged;
        Tags.CollectionChanged += (s, e) => {
            foreach (var item in Items) item.NotifyTagChanges();
        };
    }

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
    public partial ObservableCollection<PlaylistItem> Items { get; set; } = new();

    [ObservableProperty]
    [JsonPropertyName("iconType")]
    public partial PlaylistIconType IconType { get; set; } = PlaylistIconType.Playlist;

    [ObservableProperty]
    [JsonPropertyName("source")]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("tags")]
    public partial ObservableCollection<string> Tags { get; set; } = new();

    [ObservableProperty]
    [JsonPropertyName("duration")]
    public partial TimeSpan Duration { get; set; }

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

    [ObservableProperty]
    [JsonIgnore]
    public partial bool HasUnresolvedTracks { get; set; }

    // Static cache and throttler across ALL playlists
    private static readonly ConcurrentDictionary<string, ImageSource> _sharedPlaylistIconCache = new();
    private static readonly SemaphoreSlim _playlistIconSemaphore = new(4, 4);

    private ImageSource? _iconImageSource;
    private bool _isGeneratingIcon;
    private bool _recalcPending;

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
        IconType = PlaylistIconType.YouTube; // Temp override, delete later
        Task.Run(async () =>
        {
            try
            {
                await _playlistIconSemaphore.WaitAsync();

                if (_sharedPlaylistIconCache.TryGetValue(cacheKey, out var cachedSource))
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _iconImageSource = cachedSource;
                        OnPropertyChanged(nameof(IconImageSource));
                    });
                    return;
                }

                const int playlistIconUiSize = 110; // TODO: make this dynamic based on actual UI size
                const int playlistGeneratedIconLogicalSize = 12;

                byte[]? imageBytes = null;
                bool isGenerator = false;
                bool isFileFallback = false;

                if (string.IsNullOrEmpty(IconUrl))
                {
                    isGenerator = true;
                }
                else if (IconUrl.StartsWith("data:image"))
                {
                    var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                    imageBytes = Convert.FromBase64String(base64Data);
                }
                else if (File.Exists(IconUrl))
                {
                    imageBytes = await File.ReadAllBytesAsync(IconUrl);
                }
                else
                {
                    isFileFallback = true;
                }

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        ImageSource? source = null;

                        if (isGenerator)
                        {
                            source = IconGenerator.GenerateIcon(Name, playlistGeneratedIconLogicalSize, playlistIconUiSize);
                        }
                        else if (imageBytes != null)
                        {
                            source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, playlistIconUiSize)
                                     ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                        }
                        else if (isFileFallback)
                        {
                            source = ImageSource.FromFile(IconUrl);
                        }

                        if (source != null)
                        {
                            _sharedPlaylistIconCache[cacheKey] = source;
                            _iconImageSource = source;
                            OnPropertyChanged(nameof(IconImageSource));
                        }
                    }
                    catch { }
                });
            }
            catch { }
            finally
            {
                _playlistIconSemaphore.Release();
            }
        });
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (PlaylistItem item in e.NewItems) item.Playlist = this;
        }
        if (e.OldItems != null)
        {
            foreach (PlaylistItem item in e.OldItems) item.Playlist = null;
        }
        RecalculateDuration();
    }

    // Catches cases where the entire Items collection is replaced
    partial void OnItemsChanged(ObservableCollection<PlaylistItem> oldValue, ObservableCollection<PlaylistItem> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= Items_CollectionChanged;
        if (newValue != null)
        {
            newValue.CollectionChanged += Items_CollectionChanged;
            foreach (var item in newValue) item.Playlist = this;
        }
        RecalculateDuration();
    }

    [JsonIgnore]
    public string DisplayDuration
    {
        get
        {
            string suffix = HasUnresolvedTracks ? "+" : "";

            if (Duration.TotalHours >= 1)
                return $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}{suffix}";
            return $"{Duration.Minutes:D2}:{Duration.Seconds:D2}{suffix}";
        }
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DisplayDuration));
    }

    partial void OnHasUnresolvedTracksChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayDuration));
    }

    public void RequestRecalculateDuration()
    {
        if (_recalcPending) return;
        _recalcPending = true;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _recalcPending = false;
            RecalculateDuration();
        });
    }

    public void RecalculateDuration()
    {
        double totalSeconds = 0;
        bool hasUnresolved = false;

        foreach (var item in Items)
        {
            // If length is unknown, it actually has a URL, and we HAVEN'T attempted to resolve it yet
            if (item.Duration.TotalSeconds <= 0 && !string.IsNullOrEmpty(item.Url) && !item.IsResolutionAttempted)
            {
                hasUnresolved = true;
            }
            totalSeconds += item.Duration.TotalSeconds;
        }

        Duration = TimeSpan.FromSeconds(totalSeconds);
        HasUnresolvedTracks = hasUnresolved;
    }

    partial void OnIconUrlChanged(string value)
    {
        _iconImageSource = null;
        _isGeneratingIcon = false;
        OnPropertyChanged(nameof(IconImageSource));
    }
}