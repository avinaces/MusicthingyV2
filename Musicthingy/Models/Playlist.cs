using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // Add this to your Playlist.cs class
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

            if (string.IsNullOrEmpty(IconUrl))
            {
                // Passing 140 because XAML Playlist Image has WidthRequest="140"
                _iconImageSource = IconGenerator.GenerateIcon(Name, 12, 140);  //TODO: Just guessing the uiSize is BAD
            }
            else if (IconUrl.StartsWith("data:image"))
            {
                var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                var imageBytes = Convert.FromBase64String(base64Data);
                _iconImageSource = ImageSource.FromStream(() => new MemoryStream(imageBytes));
            }
            else
            {
                _iconImageSource = ImageSource.FromFile(IconUrl);
            }

            return _iconImageSource;
        }
    }

    partial void OnIconUrlChanged(string value)
    {
        _iconImageSource = null;
        OnPropertyChanged(nameof(IconImageSource));
    }
}