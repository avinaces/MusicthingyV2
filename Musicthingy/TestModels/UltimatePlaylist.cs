using System.Collections.ObjectModel;

namespace Musicthingy.TestModels;

public class UltimatePlaylist
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public ObservableCollection<UltimateItem> Items { get; set; } = new();

    public int ItemCount => Items.Count;
}