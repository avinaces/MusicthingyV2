using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Diagnostics;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private static readonly string[] androidFileTypeJSON = ["application/json"];
    private static readonly string[] windowsFileTypeJSON = [".json"];

    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}", CreatedAt = DateTime.Now };
        Playlists.Add(newList);
        SelectedPlaylist = newList;

        RequestSaveLibrary(); // Trigger library save
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, windowsFileTypeJSON },
                    { DevicePlatform.Android, androidFileTypeJSON },
                });

            // Use PickMultipleAsync to allow multiple file selection
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select Playlist JSON files",
                FileTypes = customFileType
            });

            if (results != null)
            {
                var importedPlaylists = new List<Playlist>();

                // Loop through all selected files
                foreach (var result in results)
                {
                    using var stream = await result.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    var jsonContent = await reader.ReadToEndAsync();

                    var importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);

                    if (importedPlaylist != null)
                    {
                        importedPlaylists.Add(importedPlaylist);
                    }
                }

                if (importedPlaylists.Count > 0)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // Add all successfully imported playlists to the UI
                        foreach (var playlist in importedPlaylists)
                        {
                            Playlists.Add(playlist);
                        }

                        // Set the last imported playlist as the currently selected one
                        SelectedPlaylist = importedPlaylists[importedPlaylists.Count - 1];

                        RequestSaveLibrary(); // Trigger library save
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", $"Failed to import: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        // Uses the overlay system we built earlier
        bool confirm = await ShowConfirmAsync(
            title: "Delete Playlist",
            message: $"Are you sure you want to delete '{playlist.Name}'? This cannot be undone.",
            confirmText: "Delete",
            bgBehavior: ModalBackgroundTapBehavior.Cancel,
            isDanger: true);

        if (confirm)
        {
            Playlists.Remove(playlist);
            if (SelectedPlaylist == playlist) SelectedPlaylist = null;

            RequestSaveLibrary(); // Trigger library save
        }
    }

    [RelayCommand]
    private void DeleteTrack(PlaylistItem track)
    {
        // TODO: add delete modal
        if (track.Playlist != null && track.Playlist.Items.Contains(track))
        {
            track.Playlist.Items.Remove(track);
            PlaybackQueue.Remove(track);
            RequestSaveLibrary();
        }
    }

    // NEW: Scrapes all current tags dynamically, mixed with cached ones
    public List<string> GetAllKnownTags()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Gather from Playlists
        foreach (var p in Playlists)
        {
            if (p.Tags != null)
                foreach (var t in p.Tags) tags.Add(t);

            if (p.Items != null)
                foreach (var i in p.Items)
                    if (i.Tags != null)
                        foreach (var t in i.Tags) tags.Add(t);
        }

        // Gather from Global Cache (for tags that were created but not assigned to anything yet)
        var storedTagsJson = Preferences.Default.Get("AppGlobalTags", "[]");
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<List<string>>(storedTagsJson);
            if (stored != null)
            {
                foreach (var t in stored) tags.Add(t);
            }
        }
        catch { }

        return tags.ToList();
    }

    [RelayCommand]
    private async Task EditTrackAsync(PlaylistItem track)
    {
        var allTags = GetAllKnownTags();

        // Create the View
        var sheet = new Views.EditTrackSheet(track, allTags);

        // Await it native natively
        var result = await Services.SheetManager.ShowAsync<bool>(sheet);

        if (result)
        {
            RequestSaveLibrary();
        }
    }

    private void LoadDummyData()
    {
        Playlist dummyList = new()
        {
            Name = "Test",
            CreatedAt = DateTime.Now
        };

        dummyList.Items.Add(new PlaylistItem { Title = "Skarrsinger Karmelita", Url = "https://www.youtube.com/watch?v=dhc4WFEeZfw&list=RDdhc4WFEeZfw&start_radio=1", StartTime = 5466, EndTime = 5573 });
        dummyList.Items.Add(new PlaylistItem { Title = "76", Url = "https://static.bandlab.com/revisions-formatted/7277ea00-254d-9b8c-0eab-c6b7bf94e414/7277ea00-254d-9b8c-0eab-c6b7bf94e414.m4a" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Empty", CreatedAt = DateTime.Now });

        SelectedPlaylist = dummyList;
    }
}