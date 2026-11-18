using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using Musicthingy.Views;
using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private static readonly string[] androidFileTypeJSON = ["application/json"];
    private static readonly string[] windowsFileTypeJSON = [".json"];

    private Playlist? _cachedTracksPlaylist;
    private int _cachedTracksVersion;
    private int _cachedTracksBuiltVersion = -1;
    private int _cachedTracksRefreshPending;

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

        // Listen for future cache downloads/deletions in the background.
        // The handler only rebuilds the list while it is the visible
        // playlist — a full rebuild per completed download used to run on
        // the UI thread and freeze the app (ANR).
        _trackCacheService.CacheUpdated += OnTrackCacheUpdated;

        // Listen for when the user imports tracks from a YouTube Playlist
        WeakReferenceMessenger.Default.Register<ImportPlaylistTracksMessage>(this, (r, m) =>
        {
            if (m.OriginalTrack.Playlist == null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Playlist playlist = m.OriginalTrack.Playlist;
                int insertIndex = playlist.Items.IndexOf(m.OriginalTrack);

                if (insertIndex >= 0)
                {
                    playlist.Items.RemoveAt(insertIndex);
                }
                else
                {
                    insertIndex = playlist.Items.Count;
                }

                List<PlaylistItem> newTracks = new();

                foreach (YouTubePlaylistVideo video in m.Videos)
                {
                    PlaylistItem newTrack = new PlaylistItem
                    {
                        Title = video.Title,
                        Url = video.Url,
                        IconUrl = video.ThumbnailUrl ?? string.Empty,
                        Duration = video.Duration ?? TimeSpan.Zero,
                        Playlist = playlist
                    };

                    newTracks.Add(newTrack);
                }

                for (int i = 0; i < newTracks.Count; i++)
                {
                    playlist.Items.Insert(insertIndex + i, newTracks[i]);
                }

                playlist.RequestRecalculateDuration();
                UpdateFilterTags();
                RefreshDisplayedItems();
                RequestSaveLibrary();
            });
        });

        // Listen for when the user chooses to split a track into chapters
        WeakReferenceMessenger.Default.Register<SplitTrackIntoChaptersMessage>(this, (r, m) =>
        {
            if (m.OriginalTrack.Playlist == null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var playlist = m.OriginalTrack.Playlist;
                int insertIndex = playlist.Items.IndexOf(m.OriginalTrack);

                // If the track was already in the playlist (Editing mode), remove it
                if (insertIndex >= 0)
                {
                    playlist.Items.RemoveAt(insertIndex);
                }
                else
                {
                    // If it was a "New Track", it isn't in the list yet, so just append to the end
                    insertIndex = playlist.Items.Count;
                }

                var newTracks = new List<PlaylistItem>();

                for (int i = 0; i < m.Chapters.Count; i++)
                {
                    var chapter = m.Chapters[i];

                    // Fix: Cast the double to int
                    int startTime = (int)chapter.Timestamp.TotalSeconds;
                    int? endTime = null;

                    if (i < m.Chapters.Count - 1)
                    {
                        // Fix: Cast the double to int
                        endTime = (int)m.Chapters[i + 1].Timestamp.TotalSeconds;
                    }

                    var newTrack = new PlaylistItem
                    {
                        Title = chapter.Title,
                        Url = m.VideoUrl,
                        IconUrl = m.IconUrl,
                        StartTime = startTime,
                        EndTime = endTime,
                        Playlist = playlist
                    };

                    newTracks.Add(newTrack);
                }

                // Insert the new tracks precisely where the original one was
                for (int i = 0; i < newTracks.Count; i++)
                {
                    playlist.Items.Insert(insertIndex + i, newTracks[i]);
                }

                playlist.RequestRecalculateDuration();
                UpdateFilterTags();
                RefreshDisplayedItems();
                RequestSaveLibrary();
            });
        });
    }

    private void OnTrackCacheUpdated(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _cachedTracksVersion);

        // Only rebuild a list that is on screen; otherwise the next selection
        // does it. CacheUpdated fires after every completed background
        // download, and the rebuild scans the whole library.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedPlaylist == _cachedTracksPlaylist)
                RefreshCachedTracksPlaylist();
        });
    }

    /// <summary>
    /// Rebuilds the auto-generated Cached Tracks playlist from the files on
    /// disk. The scan runs on a worker thread; only the final row swap touches
    /// the UI thread. Calls are coalesced because CacheUpdated fires after
    /// every background download.
    /// </summary>
    private void RefreshCachedTracksPlaylist()
    {
        if (_cachedTracksPlaylist == null) return;

        // One scan at a time. The version recorded here is marked as built
        // only after the rows are applied, so a cache change that lands
        // mid-scan triggers exactly one more pass.
        if (Interlocked.CompareExchange(ref _cachedTracksRefreshPending, 1, 0) != 0)
            return;

        int version = Volatile.Read(ref _cachedTracksVersion);

        // Snapshot the UI-owned collections before leaving the UI thread.
        (Playlist Owner, PlaylistItem[] Items)[] sources = Playlists
            .Where(p => !p.IsSystemPlaylist)
            .Select(p => (Owner: p, Items: p.Items.ToArray()))
            .ToArray();

        _ = Task.Run(() =>
        {
            List<(PlaylistItem Track, Playlist Owner)> snapshot;
            try
            {
                snapshot = BuildCachedTracksSnapshot(sources);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cache] Cached Tracks scan failed: {ex.Message}");
                Interlocked.Exchange(ref _cachedTracksRefreshPending, 0);
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    ApplyCachedTracksSnapshot(snapshot);
                    Volatile.Write(ref _cachedTracksBuiltVersion, version);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cache] Cached Tracks apply failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _cachedTracksRefreshPending, 0);
                }

                // A cache event that landed mid-scan needs one more pass.
                if (Volatile.Read(ref _cachedTracksVersion) !=
                    Volatile.Read(ref _cachedTracksBuiltVersion) &&
                    SelectedPlaylist == _cachedTracksPlaylist)
                {
                    RefreshCachedTracksPlaylist();
                }
            });
        });
    }

    /// <summary>
    /// Worker-thread half: one directory listing, then a SHA256 per track URL
    /// (no file-system call per track).
    /// </summary>
    private List<(PlaylistItem Track, Playlist Owner)> BuildCachedTracksSnapshot(
        (Playlist Owner, PlaylistItem[] Items)[] sources)
    {
        HashSet<string> cachedIds = _trackCacheService.GetCachedFileIds();
        HashSet<string> uniqueKeys = new(StringComparer.Ordinal);
        List<(PlaylistItem, Playlist)> result = new();

        foreach ((Playlist owner, PlaylistItem[] items) in sources)
        {
            foreach (PlaylistItem track in items)
            {
                if (string.IsNullOrEmpty(track.Url)) continue;
                if (!cachedIds.Contains(TrackCacheService.ComputeCacheId(track.Url))) continue;

                // Unique footprint — the same URL can appear as several slices.
                string key = $"{track.Url}::{track.StartTime ?? 0}::{track.EndTime ?? 0}";
                if (uniqueKeys.Add(key)) result.Add((track, owner));
            }
        }

        return result;
    }

    /// <summary>
    /// UI-thread half: swap the rows in one pass and rebuild DisplayedItems
    /// once. The selection listener is detached during the swap, otherwise
    /// every added row would trigger a full list refresh (O(n²)).
    /// </summary>
    private void ApplyCachedTracksSnapshot(List<(PlaylistItem Track, Playlist Owner)> snapshot)
    {
        if (_cachedTracksPlaylist == null) return;

        bool isVisible = SelectedPlaylist == _cachedTracksPlaylist;

        if (isVisible)
            _cachedTracksPlaylist.Items.CollectionChanged -= SelectedPlaylistItems_CollectionChanged;

        try
        {
            List<PlaylistItem> rows = new(snapshot.Count);

            foreach ((PlaylistItem source, Playlist owner) in snapshot)
            {
                PlaylistItem pointerTrack = new()
                {
                    Title = source.Title,
                    Url = source.Url,
                    StartTime = source.StartTime,
                    EndTime = source.EndTime,
                    IconUrl = source.IconUrl,
                    Duration = source.Duration,
                    Lyrics = source.Lyrics,
                    Views = source.Views,
                    LastPlayedAt = source.LastPlayedAt,
                    Playlist = _cachedTracksPlaylist,
                    SourcePlaylist = owner,
                    SourceTrack = source
                };

                if (source.Tags != null)
                {
                    foreach (string tag in source.Tags) pointerTrack.Tags.Add(tag);
                }

                rows.Add(pointerTrack);
            }

            _cachedTracksPlaylist.ReplaceItems(rows);
        }
        finally
        {
            if (isVisible)
                _cachedTracksPlaylist.Items.CollectionChanged += SelectedPlaylistItems_CollectionChanged;
        }

        if (isVisible)
        {
            UpdateFilterTags();
            RefreshDisplayedItems(forceReset: true);
        }

        // Cached Tracks changes do not go through RequestSaveLibrary.
        RefreshSearchIndex();
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        return; // Dynamic theming disabled temporarily
    }

    /// <summary>
    /// Deletes one track's cached file (Cached Tracks → "Delete from Cache").
    /// The list refreshes itself through TrackCacheService.CacheUpdated, which
    /// also updates the Storage stats card. The delete runs off the UI thread:
    /// File.Delete on a multi-megabyte cache file used to block the main
    /// thread long enough to trip the ANR watchdog.
    /// </summary>
    public void RemoveTrackFromCache(PlaylistItem track)
    {
        if (string.IsNullOrEmpty(track.Url)) return;

        string url = track.Url;

        _ = Task.Run(() =>
        {
            bool removed = _trackCacheService.DeleteCachedTrack(url);
            Debug.WriteLine(removed
                ? $"[Cache] Removed from cache by user: {url}"
                : $"[Cache] No cached file for {url}");
        });
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