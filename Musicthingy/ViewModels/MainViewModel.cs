using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using Musicthingy.Services;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // CHANGED: Now using public partial properties
    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new(); [ObservableProperty]
    public partial Playlist? SelectedPlaylist { get; set; }

    private readonly PlaylistDataService _dataService;

    public MainViewModel()
    {
        _dataService = new PlaylistDataService();
        LoadDummyData();
    }

    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}", IconUrl = "dotnet_bot.png" };
        Playlists.Add(newList);
        SelectedPlaylist = newList;
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        try
        {
            // Define platform-specific types for JSON
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".json" } },
                    { DevicePlatform.Android, new[] { "application/json" } },
                    { DevicePlatform.iOS, new[] { "public.json" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.json" } },
                });

            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Playlist JSON file",
                FileTypes = customFileType
            });

            if (result != null)
            {
                using var stream = await result.OpenReadAsync();
                using var reader = new StreamReader(stream);
                var jsonContent = await reader.ReadToEndAsync();

                var importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);

                if (importedPlaylist != null)
                {
                    // Update UI on the main thread
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Playlists.Add(importedPlaylist);
                        SelectedPlaylist = importedPlaylist;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", $"Failed to import: {ex.Message}", "OK");
        }
    }

    private void LoadDummyData()
    {
        var dummyList = new Playlist
        {
            Name = "Chill Vibes",
            IconUrl = "dotnet_bot.png",
            CreatedAt = DateTime.Now
        };

        dummyList.Items.Add(new PlaylistItem { Title = "Lofi Hip Hop", Url = "https://youtube.com/watch?v=jfKfPfyJRdk" });
        dummyList.Items.Add(new PlaylistItem { Title = "Synthwave Mix", Url = "https://youtube.com/watch?v=MVPTGNGiI" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Workout", IconUrl = "dotnet_bot.png", CreatedAt = DateTime.Now });

        SelectedPlaylist = dummyList;
    }
}