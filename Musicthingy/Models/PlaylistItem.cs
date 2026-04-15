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

    // Cache the image so it only generates ONCE!
    private ImageSource? _itemIconSource;
    public ImageSource ItemIconSource
    {
        get
        {
            if (_itemIconSource != null) return _itemIconSource;

            if (string.IsNullOrEmpty(IconUrl))
            {
                // Passing 36 because XAML Image has WidthRequest="36"
                _itemIconSource = IconGenerator.GenerateIcon(Title, 8, 36); //TODO: Just guessing the uiSize is BAD
            }
            else if (IconUrl.StartsWith("data:image"))
            {
                var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
                _itemIconSource = ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(base64Data)));
            }
            else
            {
                _itemIconSource = ImageSource.FromFile(IconUrl);
            }

            return _itemIconSource;
        }
    }

    // Reset the cache if the IconUrl ever changes
    partial void OnIconUrlChanged(string value)
    {
        _itemIconSource = null;
        OnPropertyChanged(nameof(ItemIconSource));
    }

    public bool HasTags => Tags.Count > 0;

    [RelayCommand] private void OnPointerEntered() => IsHovered = true; [RelayCommand] private void OnPointerExited() => IsHovered = false;
}