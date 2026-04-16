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

    public bool HasTags => Tags.Count > 0;

    // Maximum of 4 visual tags for the list view
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

            // Kick off the generation in the background so the UI thread doesn't wait
            GenerateIconAsync();

            return null;
        }
    }

    private async void GenerateIconAsync()
    {
        ImageSource source;

        if (string.IsNullOrEmpty(IconUrl))
        {
            // Run the heavy lifting on a background thread!
            source = await Task.Run(() => IconGenerator.GenerateIcon(Title, 8, 36));
        }
        else if (IconUrl.StartsWith("data:image"))
        {
            var base64Data = IconUrl.Substring(IconUrl.IndexOf(',') + 1);
            source = await Task.Run(() => ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(base64Data))));
        }
        else
        {
            source = ImageSource.FromFile(IconUrl);
        }

        _itemIconSource = source;
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ItemIconSource)));
    }

    [RelayCommand] private void OnPointerEntered() => IsHovered = true; [RelayCommand] private void OnPointerExited() => IsHovered = false;
}