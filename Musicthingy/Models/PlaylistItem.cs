using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Musicthingy.Models;

public partial class PlaylistItem : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string url = string.Empty;

    [ObservableProperty]
    private string startTime = string.Empty;

    [ObservableProperty]
    private string endTime = string.Empty;

    [ObservableProperty]
    private bool isHovered;

    [ObservableProperty]
    private bool isSelected;

    public string DisplayUrl => string.IsNullOrEmpty(Url) ? "" : new Uri(Url).Host;

    [RelayCommand] private void OnPointerEntered() => IsHovered = true;
    [RelayCommand] private void OnPointerExited() => IsHovered = false;
}