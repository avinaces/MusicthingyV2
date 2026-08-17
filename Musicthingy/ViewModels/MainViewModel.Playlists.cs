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


    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        return; // Disable dynamic theming for now, as it can cause jank and isn't the most important feature. Will revisit later.
        Task.Run(async () =>
        {
            if (value != null)
            {
                var extractedColor = await _colorExtractionService.ExtractDominantColorAsync(value);

                if (extractedColor != null)
                {
                    // Apply the new playlist theme!
                    MainThread.BeginInvokeOnMainThread(() => _themeManager.ApplyTheme(extractedColor));
                }
                else
                {
                    // No icon/color found? Revert to the standard system accent color
                    var fallbackColor = Services.SystemThemeHelper.GetAndroidSystemAccentColor();
                    if (fallbackColor != null)
                        MainThread.BeginInvokeOnMainThread(() => _themeManager.ApplyTheme(fallbackColor));
                }
            }
        });
    }

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
            IEnumerable<FileResult>? results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select Playlist JSON files",
                FileTypes = customFileType
            });

            if (results != null && results.Any())
            {
                List<Playlist> importedPlaylists = new List<Playlist>();

                // Loop through all selected files
                foreach (FileResult result in results)
                {
                    using Stream stream = await result.OpenReadAsync();
                    using StreamReader reader = new StreamReader(stream);
                    string jsonContent = await reader.ReadToEndAsync();

                    Playlist? importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);

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
                        foreach (Playlist playlist in importedPlaylists)
                        {
                            Playlists.Add(playlist);
                        }

                        // Set the last imported playlist as the currently selected one
                        SelectedPlaylist = importedPlaylists.Last();

                        RequestSaveLibrary(); // Trigger library save
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await ShowConfirmAsync(
                    title: "Error",
                    message: $"Failed to import: {ex.Message}",
                    confirmText: "OK",
                    bgBehavior: ModalBackgroundTapBehavior.Confirm,
                    isDanger: true);
            });
        }
    }

    [RelayCommand]
    private async Task ExportPlaylistAsync(Playlist playlist)
    {
        if (playlist == null) return;

        try
        {
            // 1. Serialize the Playlist model directly (No DTO needed!)
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true, // Makes the JSON file pretty and readable
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(playlist, options);

            // 2. Clean invalid file name chars safely
            string safeName = string.IsNullOrWhiteSpace(playlist.Name) ? "Playlist" : playlist.Name;
            string cleanName = string.Join("_", safeName.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"{cleanName}.json";

            // 3. Save file natively
            using MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var fileSaverResult = await CommunityToolkit.Maui.Storage.FileSaver.Default.SaveAsync(fileName, stream, CancellationToken.None);

            if (fileSaverResult.IsSuccessful)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await ShowConfirmAsync(
                        title: "Success",
                        message: $"Playlist exported to:\n{fileSaverResult.FilePath}",
                        confirmText: "OK",
                        bgBehavior: ModalBackgroundTapBehavior.Confirm);
                });
            }
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await ShowConfirmAsync(
                    title: "Export Failed",
                    message: ex.Message,
                    confirmText: "OK",
                    bgBehavior: ModalBackgroundTapBehavior.Confirm,
                    isDanger: true);
            });
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
    private async Task AddNewTrackAsync()
    {
        if (SelectedPlaylist == null) return;

        // 1. Create the temporary new track
        var newTrack = new PlaylistItem
        {
            Playlist = SelectedPlaylist
        };

        var allTags = GetAllKnownTags();

        // 2. Open the edit options for that item
        var sheet = new Views.EditTrackSheet(newTrack, allTags);
        var result = await Services.SheetManager.ShowAsync<bool>(sheet);

        // 3. If the user saved their edits, append it to the active playlist
        if (result)
        {
            SelectedPlaylist.Items.Add(newTrack);

            UpdateFilterTags();
            RefreshDisplayedItems();
            RequestSaveLibrary();
        }
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
            UpdateFilterTags();
            RefreshDisplayedItems();
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
            UpdateFilterTags();
            RefreshDisplayedItems();
            RequestSaveLibrary();
        }
    }
}