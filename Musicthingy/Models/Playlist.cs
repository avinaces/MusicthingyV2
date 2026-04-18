using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services; // Ensure namespace for ImageScalingService
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Musicthingy.Models;

public partial class Playlist : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("name")]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
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
    public partial string IconType { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("source")]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    [JsonPropertyName("tags")]
    public partial ObservableCollection<string> Tags { get; set; } = new();

    [ObservableProperty]
    public partial bool IsHovered { get; set; }

    [RelayCommand]
    private void OnPointerEntered() => IsHovered = true;

    [RelayCommand]
    private void OnPointerExited() => IsHovered = false;

    [JsonIgnore]
    public int ItemCount => Items.Count;

    private ImageSource? _iconImageSource;

    [JsonIgnore]
    public ImageSource? IconImageSource
    {
        get
        {
            if (_iconImageSource != null) return _iconImageSource;

            GenerateIconAsync();

            return null;
        }
    }

    private async void GenerateIconAsync()
    {

        try
        {
            ImageSource? source = null;

            if (string.IsNullOrEmpty(IconUrl))
            {
                // Playlist visual size usually 140
                source = await Task.Run(() => IconGenerator.GenerateIcon(Name, 12, 140));
            }
            else if (IconUrl.StartsWith("data:image"))
            {
                var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                var imageBytes = Convert.FromBase64String(base64Data);
                source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, 140)
                         ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
            }
            else if (File.Exists(IconUrl))
            {
                var imageBytes = await File.ReadAllBytesAsync(IconUrl);
                source = await ImageScalingService.ScaleIfSmallAsync(imageBytes, 140)
                         ?? ImageSource.FromStream(() => new MemoryStream(imageBytes));
            }
            else
            {
                // Fallback for MauiAssets or paths that aren't raw files
                source = ImageSource.FromFile(IconUrl);
            }

            _iconImageSource = source;
            MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(IconImageSource)));
        }
        catch(Exception ex) {
            await Shell.Current.DisplayAlertAsync("Error", $"Failed to generate icon: {ex.Message}", "OK");
        }
    }

    partial void OnIconUrlChanged(string value)
    {
        _iconImageSource = null;
        OnPropertyChanged(nameof(IconImageSource));
    }
}