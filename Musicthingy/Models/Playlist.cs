using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

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

    [JsonIgnore]
    public int ItemCount => Items.Count;

    [JsonIgnore]
    public ImageSource? IconImageSource
    {
        get
        {
            if (string.IsNullOrEmpty(IconUrl)) return ImageSource.FromFile("dotnet_bot.png");

            if (IconUrl.StartsWith("data:image"))
            {
                var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                var imageBytes = Convert.FromBase64String(base64Data);
                return ImageSource.FromStream(() => new MemoryStream(imageBytes));
            }

            return ImageSource.FromFile(IconUrl);
        }
    }

    // Logic remains the same: notify the UI that the image source 
    // needs to re-evaluate when the string URL changes.
    partial void OnIconUrlChanged(string value)
    {
        OnPropertyChanged(nameof(IconImageSource));
    }
}