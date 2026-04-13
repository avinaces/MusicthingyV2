using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Musicthingy.Models;

public partial class Playlist : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IconUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> Items { get; set; } = new();

    public int ItemCount => Items.Count;
}