using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Threading;

namespace Musicthingy.Models;

public partial class PlaylistItem : ObservableObject
{
    public PlaylistItem()
    {
        Tags.CollectionChanged += (s, e) => NotifyTagChanges();
    }

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Url { get; set; } = string.Empty;
    [ObservableProperty] public partial int? StartTime { get; set; }
    [ObservableProperty] public partial int? EndTime { get; set; }
    [ObservableProperty] public partial string IconUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> Tags { get; set; } = new();
    [ObservableProperty] public partial bool IsHovered { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Lyrics { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime? LastPlayedAt { get; set; }
    [ObservableProperty] public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial Playlist? Playlist { get; set; }

    [JsonIgnore]
    public bool IsResolutionAttempted { get; set; }

    // Helper to combine Playlist tags and Item tags
    private List<string> AllDisplayTags =>
        (Playlist?.Tags ?? Enumerable.Empty<string>())
        .Concat(Tags)
        .ToList();

    public bool HasTags => AllDisplayTags.Count > 0;

    public string Tag1 => AllDisplayTags.Count > 0 ? AllDisplayTags[0] : "";
    public bool HasTag1 => AllDisplayTags.Count > 0;

    public string Tag2 => AllDisplayTags.Count > 1 ? AllDisplayTags[1] : "";
    public bool HasTag2 => AllDisplayTags.Count > 1;

    public string Tag3 => AllDisplayTags.Count > 2 ? AllDisplayTags[2] : "";
    public bool HasTag3 => AllDisplayTags.Count > 2;

    public string Tag4 => AllDisplayTags.Count > 3 ? AllDisplayTags[3] : "";
    public bool HasTag4 => AllDisplayTags.Count > 3;

    // Call this to refresh the UI tags
    public void NotifyTagChanges()
    {
        OnPropertyChanged(nameof(Tag1));
        OnPropertyChanged(nameof(HasTag1));
        OnPropertyChanged(nameof(Tag2));
        OnPropertyChanged(nameof(HasTag2));
        OnPropertyChanged(nameof(Tag3));
        OnPropertyChanged(nameof(HasTag3));
        OnPropertyChanged(nameof(Tag4));
        OnPropertyChanged(nameof(HasTag4));
    }

    // Ensure UI updates if the playlist reference is changed
    partial void OnPlaylistChanged(Playlist? value) => NotifyTagChanges();

    public string DisplayUrl
    {
        get
        {
            if (string.IsNullOrEmpty(Url)) return "";
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return uri.Host;
            return "";
        }
    }

    // Static cache and throttler across ALL playlist items
    private static readonly ConcurrentDictionary<string, ImageSource> _sharedItemIconCache = new();
    private static readonly SemaphoreSlim _itemIconSemaphore = new(4, 4);

    private ImageSource? _itemIconSource;
    private bool _isGeneratingIcon;

    [JsonIgnore]
    public ImageSource? ItemIconSource
    {
        get
        {

            //return ImageSource.FromStream(() => new MemoryStream(new byte[1]));
            if (_itemIconSource != null) return _itemIconSource;

            // Fast path: if we've generated this icon before, return it instantly to the UI
            string cacheKey = string.IsNullOrEmpty(IconUrl) ? $"TITLE_{Title}" : IconUrl;
            if (_sharedItemIconCache.TryGetValue(cacheKey, out var cachedSource))
            {
                _itemIconSource = cachedSource;
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

    [JsonIgnore]
    public string? Artist
    {
        get
        {
            // For now atleast the artist will be playlist name
            return Playlist?.Name;
        }
    }

    [JsonIgnore]
    public string DisplayDuration
    {
        get
        {
            if (Duration.TotalSeconds <= 0) return "--:--";

            if (Duration.TotalHours >= 1)
                return $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}";
            return $"{Duration.Minutes:D2}:{Duration.Seconds:D2}";
        }
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DisplayDuration));
        Playlist?.RequestRecalculateDuration();
    }

    private void GenerateIconAsync(string cacheKey)
    {
        Task.Run(async () =>
        {
            try
            {
                await _itemIconSemaphore.WaitAsync();

                if (_sharedItemIconCache.TryGetValue(cacheKey, out var cachedSource))
                {
                    MainThread.BeginInvokeOnMainThread(() => {
                        _itemIconSource = cachedSource;
                        OnPropertyChanged(nameof(ItemIconSource));
                    });
                    return;
                }

                byte[]? imageBytes = null;
                bool isGenerator = false;
                bool isFileFallback = false;

                // Do the heavy lifting (file IO / decoding) safely on the background thread
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
                        const int iconUiSize = 44; // TODO: make this dynamic based on actual UI size
                        const int generatedIconLogicalSize = 8;
                        ImageSource? source = null;

                        if (isGenerator)
                        {
                            source = IconGenerator.GenerateIcon(Title, generatedIconLogicalSize, iconUiSize);
                        }
                        else if (imageBytes != null)
                        {
                            source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, iconUiSize)
                                     ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                        }
                        else if (isFileFallback)
                        {
                            source = ImageSource.FromFile(IconUrl);
                        }

                        if (source != null)
                        {
                            _sharedItemIconCache[cacheKey] = source;
                            _itemIconSource = source;
                            OnPropertyChanged(nameof(ItemIconSource));
                        }
                    }
                    catch { /* Ignore UI thread exceptions during fast scrolling */ }
                });
            }
            catch { }
            finally
            {
                _itemIconSemaphore.Release();
            }
        });
    }

    partial void OnIconUrlChanged(string value)
    {
        _itemIconSource = null;
        _isGeneratingIcon = false;
        OnPropertyChanged(nameof(ItemIconSource));
    }

    [RelayCommand] private void OnPointerEntered() => IsHovered = true;
    [RelayCommand] private void OnPointerExited() => IsHovered = false;
}