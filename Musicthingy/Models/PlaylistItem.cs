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

    public bool HasTags => Tags.Count > 0;
    public string Tag1 => Tags.Count > 0 ? Tags[0] : "";
    public bool HasTag1 => Tags.Count > 0;
    public string Tag2 => Tags.Count > 1 ? Tags[1] : "";
    public bool HasTag2 => Tags.Count > 1;
    public string Tag3 => Tags.Count > 2 ? Tags[2] : "";
    public bool HasTag3 => Tags.Count > 2;
    public string Tag4 => Tags.Count > 3 ? Tags[3] : "";
    public bool HasTag4 => Tags.Count > 3;

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

    private void GenerateIconAsync(string cacheKey)
    {
        Task.Run(async () =>
        {
            try
            {
                // Prevent ThreadPool explosion by limiting concurrent operations
                await _itemIconSemaphore.WaitAsync();

                // Double-check cache in case another task just finished it
                if (_sharedItemIconCache.TryGetValue(cacheKey, out var cachedSource))
                {
                    _itemIconSource = cachedSource;
                    MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ItemIconSource)));
                    return;
                }

                ImageSource? source = null;

                if (string.IsNullOrEmpty(IconUrl))
                {
                    source = IconGenerator.GenerateIcon(Title, 8, 36);
                }
                else if (IconUrl.StartsWith("data:image"))
                {
                    var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                    var imageBytes = Convert.FromBase64String(base64Data);
                    source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, 36)
                             ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                }
                else if (File.Exists(IconUrl))
                {
                    var imageBytes = await File.ReadAllBytesAsync(IconUrl);
                    source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, 36)
                             ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
                }
                else
                {
                    source = ImageSource.FromFile(IconUrl);
                }

                if (source != null)
                {
                    _sharedItemIconCache[cacheKey] = source;
                    _itemIconSource = source;
                    MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ItemIconSource)));
                }
            }
            catch
            {
                // Ignore silent failures to prevent alert spam
            }
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