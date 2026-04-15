using System.Collections.ObjectModel;

namespace Musicthingy.TestModels;

public class UltimateItem
{
    public string Title { get; set; } = "";

    public ObservableCollection<string> Tags { get; set; } = new();
}