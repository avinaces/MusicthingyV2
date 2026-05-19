using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Diagnostics;
using System.Text.Json;

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
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlertAsync("Error", $"Failed to import: {ex.Message}", "OK"));
        }
    }

    [RelayCommand]
    private async Task ExportPlaylistAsync(Playlist playlist)
    {
        try
        {
            // 1. Map current Playlist to safe DTO
            var dto = new PlaylistDto
            {
                name = playlist.Name,
                icon = playlist.IconUrl,
                iconType = playlist.IconType.ToString().ToLowerInvariant(),
                source = playlist.Source,
                createdAt = playlist.CreatedAt,
                updatedAt = playlist.UpdatedAt,
                duration = playlist.Duration,
                tags = playlist.Tags.ToList(),
                items = playlist.Items.Select(i => new PlaylistItemDto
                {
                    title = i.Title,
                    url = i.Url,
                    startTime = i.StartTime,
                    endTime = i.EndTime,
                    duration = i.Duration,
                    icon = i.IconUrl,
                    tags = i.Tags.ToList(),
                    lyrics = i.Lyrics,
                    lastPlayedAt = i.LastPlayedAt
                }).ToList()
            };

            // 2. Serialize to pretty JSON
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            // 3. Clean invalid file name chars
            string cleanName = string.Join("_", playlist.Name.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"{cleanName}.json";

            // 4. Save file natively
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var fileSaverResult = await FileSaver.Default.SaveAsync(fileName, stream, CancellationToken.None);

            if (fileSaverResult.IsSuccessful)
            {
                // CRITICAL FIX: Push the UI alert back to the Main Thread
                await MainThread.InvokeOnMainThreadAsync(() =>
                    Shell.Current.DisplayAlertAsync("Success", $"Playlist exported to:\n{fileSaverResult.FilePath}", "OK"));
            }
        }
        catch (Exception ex)
        {
            // CRITICAL FIX: Push the UI alert back to the Main Thread
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlertAsync("Export Failed", ex.Message, "OK"));
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
            var stored = JsonSerializer.Deserialize<List<string>>(storedTagsJson);
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

        // Await it natively
        var result = await Services.SheetManager.ShowAsync<bool>(sheet);

        if (result)
        {
            RequestSaveLibrary();
        }
    }

    [RelayCommand]
    private async Task EditPlaylistAsync(Playlist playlist)
    {
        List<string> allTags = GetAllKnownTags();

        // Create the View
        Views.EditPlaylistSheet sheet = new Views.EditPlaylistSheet(playlist, allTags);

        // Await natively
        bool result = await Services.SheetManager.ShowAsync<bool>(sheet);

        if (result)
        {
            RequestSaveLibrary();
        }
    }
}