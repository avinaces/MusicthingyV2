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
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Playlists.Add(importedPlaylist);
                        SelectedPlaylist = importedPlaylist;
                        
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

    [RelayCommand]
    private async Task EditTrackAsync(PlaylistItem track)
    {
        var popup = new Views.EditTrackPopup(track);

        // --- Fix is here: Replaced .MainPage with .Windows.FirstOrDefault()?.Page ---
        var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;

        if (activePage != null)
        {
            var popupResult = await activePage.ShowPopupAsync<bool>(popup);

            if (popupResult?.Result is true)
            {
                RequestSaveLibrary();
            }
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