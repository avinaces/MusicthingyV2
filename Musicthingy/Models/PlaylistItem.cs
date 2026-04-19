using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services; // Ensure namespace for ImageScalingService
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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

    public string DisplayUrl => string.IsNullOrEmpty(Url) ? "" : new Uri(Url).Host;

    private ImageSource? _itemIconSource;
    public ImageSource ItemIconSource
    {
        get
        {
            if (_itemIconSource != null) return _itemIconSource;

            GenerateIconAsync();

            return null;
        }
    }

    private async void GenerateIconAsync()
    {
        ImageSource? source = null;

        if (string.IsNullOrEmpty(IconUrl))
        {
            // ListItem visual size is usually 36
            source = await Task.Run(() => IconGenerator.GenerateIcon(Title, 8, 36));
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
            // Fallback for MauiAssets
            source = ImageSource.FromFile(IconUrl);
        }

        _itemIconSource = source;
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ItemIconSource)));
    }

    [RelayCommand] private void OnPointerEntered() => IsHovered = true; [RelayCommand] private void OnPointerExited() => IsHovered = false;
}