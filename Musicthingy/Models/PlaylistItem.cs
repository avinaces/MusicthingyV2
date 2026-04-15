using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    public string DisplayUrl => string.IsNullOrEmpty(Url) ? "" : new Uri(Url).Host;


    //TODO: This is very bad for peformance, fix it
    public ImageSource ItemIconSource
    {
        get
        {
            if (string.IsNullOrEmpty(IconUrl)) return IconGenerator.GenerateFallbackIcon(Title, 8);
            if (IconUrl.StartsWith("data:image"))
            {
                var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                return ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(base64Data)));
            }
            return ImageSource.FromFile(IconUrl);
        }
    }

    public bool HasTags => Tags.Count > 0;

    [RelayCommand] private void OnPointerEntered() => IsHovered = true;
    [RelayCommand] private void OnPointerExited() => IsHovered = false;
}