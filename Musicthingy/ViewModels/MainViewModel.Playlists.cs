using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using Musicthingy.Views;
using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private static readonly string[] androidFileTypeJSON = ["application/json"];
    private static readonly string[] windowsFileTypeJSON = [".json"];

    private Playlist? _cachedTracksPlaylist;

    public void InitializeSystemPlaylists()
    {
        // Find or create the auto-generated Cached Tracks playlist
        _cachedTracksPlaylist = Playlists.FirstOrDefault(p => p.IsSystemPlaylist && p.Name == "Cached Tracks");

        if (_cachedTracksPlaylist == null)
        {
            _cachedTracksPlaylist = new Playlist
            {
                Name = "Cached Tracks",
                IsSystemPlaylist = true,
                IconUrl = "refresh_cw_icon.png"
            };
            Playlists.Insert(0, _cachedTracksPlaylist); // Put it at the very top
        }

        RefreshCachedTracksPlaylist();

        // Listen for future cache downloads/deletions in the background
        _trackCacheService.CacheUpdated += (s, e) => RefreshCachedTracksPlaylist();
    }

    private void RefreshCachedTracksPlaylist()
    {
        if (_cachedTracksPlaylist == null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _cachedTracksPlaylist.Items.Clear();

            // We now track uniqueness by URL + StartTime + EndTime
            var uniqueTrackKeys = new HashSet<string>();

            // Scan all normal playlists for tracks that are currently in the cache
            foreach (var p in Playlists.Where(x => !x.IsSystemPlaylist))
            {
                foreach (var t in p.Items)
                {
                    // Create a unique footprint for this specific track slice
                    string trackKey = $"{t.Url}::{t.StartTime ?? 0}::{t.EndTime ?? 0}";

                    if (_trackCacheService.IsTrackCached(t.Url) && uniqueTrackKeys.Add(trackKey))
                    {
                        // Create a pointer copy that links to the original track and playlist
                        var pointerTrack = new PlaylistItem
                        {
                            Title = t.Title,
                            Url = t.Url,
                            StartTime = t.StartTime,
                            EndTime = t.EndTime,
                            IconUrl = t.IconUrl,
                            Duration = t.Duration,
                            Lyrics = t.Lyrics,
                            Views = t.Views,
                            LastPlayedAt = t.LastPlayedAt,
                            Playlist = _cachedTracksPlaylist,
                            SourcePlaylist = p,
                            SourceTrack = t
                        };

                        if (t.Tags != null)
                        {
                            foreach (var tag in t.Tags) pointerTrack.Tags.Add(tag);
                        }

                        _cachedTracksPlaylist.Items.Add(pointerTrack);
                    }
                }
            }
            _cachedTracksPlaylist.RequestRecalculateDuration();
        });
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        return; // Dynamic theming disabled temporarily
    }

    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}", CreatedAt = DateTime.Now };
        Playlists.Add(newList);
        SelectedPlaylist = newList;

        RequestSaveLibrary();
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        // (Keep your existing ImportPlaylistAsync logic here...)
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, windowsFileTypeJSON },
                    { DevicePlatform.Android, androidFileTypeJSON },
                });

            IEnumerable<FileResult>? results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select Playlist JSON files",
                FileTypes = customFileType
            });

            if (results != null && results.Any())
            {
                List<Playlist> importedPlaylists = new List<Playlist>();

                foreach (FileResult result in results)
                {
                    using Stream stream = await result.OpenReadAsync();
                    using StreamReader reader = new StreamReader(stream);
                    string jsonContent = await reader.ReadToEndAsync();

                    Playlist? importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);
                    if (importedPlaylist != null) importedPlaylists.Add(importedPlaylist);
                }

                if (importedPlaylists.Count > 0)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        foreach (Playlist playlist in importedPlaylists) Playlists.Add(playlist);
                        SelectedPlaylist = importedPlaylists.Last();
                        RequestSaveLibrary();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await ShowConfirmAsync("Error", $"Failed to import: {ex.Message}", "OK", ModalBackgroundTapBehavior.Confirm, isDanger: true);
            });
        }
    }

    [RelayCommand]
    private async Task ExportPlaylistAsync(Playlist playlist)
    {
        if (playlist == null || playlist.IsSystemPlaylist) return;

        // (Keep your existing ExportPlaylistAsync logic here...)
        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(playlist, options);
            string safeName = string.IsNullOrWhiteSpace(playlist.Name) ? "Playlist" : playlist.Name;
            string cleanName = string.Join("_", safeName.Split(Path.GetInvalidFileNameChars()));

            using MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var fileSaverResult = await CommunityToolkit.Maui.Storage.FileSaver.Default.SaveAsync($"{cleanName}.json", stream, CancellationToken.None);

            if (fileSaverResult.IsSuccessful)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await ShowConfirmAsync("Success", $"Playlist exported to:\n{fileSaverResult.FilePath}", "OK", ModalBackgroundTapBehavior.Confirm);
                });
            }
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await ShowConfirmAsync("Export Failed", ex.Message, "OK", ModalBackgroundTapBehavior.Confirm, isDanger: true);
            });
        }
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        if (playlist == null || playlist.IsSystemPlaylist) return; // Safeguard

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
            RequestSaveLibrary();
        }
    }

    [RelayCommand]
    private void DeleteTrack(PlaylistItem track)
    {
        if (track.Playlist != null && track.Playlist.Items.Contains(track) && !track.Playlist.IsSystemPlaylist)
        {
            track.Playlist.Items.Remove(track);
            PlaybackQueue.Remove(track);
            RequestSaveLibrary();
        }
    }

    public List<string> GetAllKnownTags()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Playlists)
        {
            if (p.Tags != null) foreach (var t in p.Tags) tags.Add(t);
            if (p.Items != null)
                foreach (var i in p.Items)
                    if (i.Tags != null)
                        foreach (var t in i.Tags) tags.Add(t);
        }

        var storedTagsJson = Preferences.Default.Get("AppGlobalTags", "[]");
        try
        {
            var stored = JsonSerializer.Deserialize<List<string>>(storedTagsJson);
            if (stored != null) foreach (var t in stored) tags.Add(t);
        }
        catch { }

        return tags.ToList();
    }

    [RelayCommand]
    private async Task AddNewTrackAsync()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.IsSystemPlaylist) return;

        var newTrack = new PlaylistItem { Playlist = SelectedPlaylist };
        var allTags = GetAllKnownTags();

        var sheet = new Views.EditTrackSheet(newTrack, allTags);
        var result = await Services.SheetManager.ShowAsync<bool>(sheet);

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
        if (track.Playlist != null && track.Playlist.IsSystemPlaylist) return;

        var allTags = GetAllKnownTags();
        var sheet = new Views.EditTrackSheet(track, allTags);
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
        if (playlist.IsSystemPlaylist) return;

        List<string> allTags = GetAllKnownTags();
        Views.EditPlaylistSheet sheet = new Views.EditPlaylistSheet(playlist, allTags);
        bool result = await Services.SheetManager.ShowAsync<bool>(sheet);

        if (result)
        {
            UpdateFilterTags();
            RefreshDisplayedItems();
            RequestSaveLibrary();
        }
    }
}