using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new();

    [ObservableProperty]
    public partial Playlist? SelectedPlaylist { get; set; } // Nullable because nothing is selected at first

    public MainViewModel()
    {
        LoadDummyData();
    }

    [RelayCommand]
    private void AddNewPlaylist()
    {
        Playlists.Add(new Playlist { Name = $"New Playlist {Playlists.Count + 1}", IconUrl = "dotnet_bot.png" });
    }

    private void LoadDummyData()
    {
        var dummyList = new Playlist { Name = "Chill Vibes", IconUrl = "dotnet_bot.png" };
        dummyList.Items.Add(new PlaylistItem { Title = "Lofi Hip Hop", Url = "https://youtube.com/watch?v=jfKfPfyJRdk" });
        dummyList.Items.Add(new PlaylistItem { Title = "Synthwave Mix", Url = "https://youtube.com/watch?v=MVPTGNGiI" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Workout", IconUrl = "dotnet_bot.png" });

        SelectedPlaylist = dummyList;
    }
}