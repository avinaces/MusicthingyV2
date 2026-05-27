using Musicthingy.Models;
using Musicthingy.Services;
using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const string LibraryFileName = "library.json";
    private CancellationTokenSource? _saveLibraryCts;

    private readonly ThemeManager _themeManager;
    private bool _isRestoringState = true;
    private double _initialRestorePosition = 0;

    private static PlaylistIconType ParseIconType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return PlaylistIconType.Playlist;
        raw = raw.Trim();
        // Accept older lowercase values like "playlist", "track", "youtube"
        if (raw.Equals("playlist", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Playlist;
        if (raw.Equals("track", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Track;
        if (raw.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.YouTube;

        // Try to parse enum names directly
        if (Enum.TryParse<PlaylistIconType>(raw, true, out var parsed)) return parsed;

        return PlaylistIconType.Playlist;
    }

    /// <summary>
    /// Call this whenever Playlists or Tracks are added, edited, or removed.
    /// It waits 1 second before saving, preventing lag if multiple edits happen at once.
    /// </summary>
    public void RequestSaveLibrary()
    {

        _saveLibraryCts?.Cancel();
        _saveLibraryCts = new CancellationTokenSource();
        var token = _saveLibraryCts.Token;

        // CRITICAL FIX: Snapshot the ObservableCollections on the MainThread BEFORE jumping to the background.
        // Otherwise, deleting a track via SwipeView while this is iterating triggers a fatal Native crash.
        List<PlaylistDto> librarySnapshot = new();
        try
        {
            librarySnapshot = Playlists.Select(p => new PlaylistDto
            {
                name = p.Name,
                icon = p.IconUrl,
                iconType = p.IconType.ToString().ToLowerInvariant(),
                source = p.Source,
                createdAt = p.CreatedAt,
                updatedAt = p.UpdatedAt,
                duration = p.Duration,
                tags = p.Tags.ToList(),
                items = p.Items.Select(i => new PlaylistItemDto
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
            }).ToList();
        }


        catch { return; /* Ignore transient read errors during fast UI changes */ }

        Task.Delay(1000, token).ContinueWith(async t =>
        {
            if (!t.IsCanceled)
            {
                await SaveLibraryAsync(librarySnapshot);
            }
        }, TaskScheduler.Default);
    }

    private async Task SaveLibraryAsync(List<PlaylistDto> libraryData)
    {
        Debug.WriteLine("Saving Library...");
        try
        {

            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            string tempPath = path + ".tmp";

            string json = JsonSerializer.Serialize(libraryData);
            await File.WriteAllTextAsync(tempPath, json);

            // Atomically move the file (overwrites the old one safely)
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save library: {ex.Message}");
        }
    }

    private async Task SaveLibraryAsync()
    {
        Debug.WriteLine("Saving Library...");
        try
        {
            // Map the active observable objects to JSON-friendly DTOs
            var library = Playlists.Select(p => new PlaylistDto
            {
                name = p.Name,
                icon = p.IconUrl,
                iconType = p.IconType.ToString().ToLowerInvariant(),
                source = p.Source,
                createdAt = p.CreatedAt,
                updatedAt = p.UpdatedAt,
                duration = p.Duration,
                tags = p.Tags.ToList(),
                items = p.Items.Select(i => new PlaylistItemDto
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
            }).ToList();

            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            string json = JsonSerializer.Serialize(library);
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save library: {ex.Message}");
        }
    }

    public async Task LoadStateAsync()
    {
        try
        {
            LoadThemeState();
            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path);
                var library = JsonSerializer.Deserialize<List<PlaylistDto>>(json);

                if (library != null && library.Count > 0)
                {
                    var loadedPlaylists = new List<Playlist>();
                    foreach (var pDto in library)
                    {
                        var p = new Playlist
                        {
                            Name = pDto.name ?? "",
                            IconUrl = pDto.icon ?? "",
                            IconType = ParseIconType(pDto.iconType),
                            Source = pDto.source ?? "",
                            CreatedAt = pDto.createdAt ?? DateTime.Now,
                            UpdatedAt = pDto.updatedAt ?? DateTime.Now,
                            Duration = pDto.duration,
                            Tags = new System.Collections.ObjectModel.ObservableCollection<string>(pDto.tags ?? new List<string>())
                        };

                        foreach (var iDto in pDto.items ?? new List<PlaylistItemDto>())
                        {
                            p.Items.Add(new PlaylistItem
                            {
                                Title = iDto.title ?? "",
                                Url = iDto.url ?? "",
                                StartTime = iDto.startTime,
                                EndTime = iDto.endTime,
                                Duration = iDto.duration,
                                IconUrl = iDto.icon ?? "",
                                Tags = new System.Collections.ObjectModel.ObservableCollection<string>(iDto.tags ?? new List<string>()),
                                Lyrics = iDto.lyrics ?? "",
                                LastPlayedAt = iDto.lastPlayedAt
                            });
                        }
                        loadedPlaylists.Add(p);
                    }

                    // Push UI updates onto the main thread
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Playlists.Clear();
                        foreach (var lp in loadedPlaylists) Playlists.Add(lp);
                        RestorePlaybackState();
                    });

                    return; // Successfully loaded, skip dummy data
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load state: {ex.Message}");
        }

        // Fallback to dummy data if no library exists (e.g. first app launch) or loading failed
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _isRestoringState = false;
        });
    }

    private void RestorePlaybackState()
    {
        IsShuffleEnabled = Preferences.Default.Get("IsShuffleEnabled", false);
        RepeatState = (RepeatMode)Preferences.Default.Get("RepeatState", (int)RepeatMode.Off);

        string? selectedPlaylistName = Preferences.Default.Get("SelectedPlaylistName", string.Empty);
        string? queueJson = Preferences.Default.Get("PlaybackQueueData", string.Empty);
        string? currentTrackJson = Preferences.Default.Get("CurrentTrackData", string.Empty);

        _initialRestorePosition = Preferences.Default.Get("CurrentPositionSeconds", 0.0);

        if (!string.IsNullOrEmpty(selectedPlaylistName))
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Name == selectedPlaylistName);
        }

        // 1. Restore the PlaybackQueue
        if (!string.IsNullOrEmpty(queueJson))
        {
            try
            {
                var queueRefs = JsonSerializer.Deserialize<List<QueueItemRef>>(queueJson);
                if (queueRefs != null && queueRefs.Count > 0)
                {
                    PlaybackQueue.Clear();
                    foreach (var qRef in queueRefs)
                    {
                        var targetPlaylist = Playlists.FirstOrDefault(p => p.Name == qRef.Playlist);

                        // FIX: Match exact slices using StartTime and EndTime!
                        var track = targetPlaylist?.Items.FirstOrDefault(i =>
                            i.Url == qRef.Url &&
                            i.StartTime == qRef.StartTime &&
                            i.EndTime == qRef.EndTime);

                        if (track != null)
                        {
                            PlaybackQueue.Add(track);
                        }
                    }
                }
            }
            catch { /* Ignore parsing errors */ }
        }

        // 2. Restore CurrentTrack
        if (!string.IsNullOrEmpty(currentTrackJson))
        {
            try
            {
                var currentRef = JsonSerializer.Deserialize<QueueItemRef>(currentTrackJson);
                if (currentRef != null && !string.IsNullOrEmpty(currentRef.Url))
                {
                    var targetPlaylist = Playlists.FirstOrDefault(p => p.Name == currentRef.Playlist);

                    var track = targetPlaylist?.Items.FirstOrDefault(i =>
                        i.Url == currentRef.Url &&
                        i.StartTime == currentRef.StartTime &&
                        i.EndTime == currentRef.EndTime);

                    if (track != null)
                    {
                        CurrentTrack = track;
                    }
                }
            }
            catch { /* Fallback */ }
        }

        _isRestoringState = false;
    }

    public void LoadThemeState()
    {
        Color seedColor;

        // 1. Try to get the Android 12+ Material You color
        Color? androidSystemColor = SystemThemeHelper.GetAndroidSystemAccentColor();

        if (androidSystemColor != null)
        {
            // Use the OS wallpaper theme!
            seedColor = androidSystemColor;
        }
        else
        {
            // 2. Fallback for Windows or older Android: Use saved preferences
            string savedHex = Preferences.Get("AppThemeSeedColor", "#6EA0D6");
            seedColor = Color.FromArgb(savedHex);
        }

        // Apply it using your existing perfectly-tuned OKLCH logic!
        _themeManager.ApplyTheme(seedColor);
    }

    public void SaveThemeColor(Color newColor)
    {
        string hex = newColor.ToHex();
        Preferences.Set("AppThemeSeedColor", hex);

        _themeManager.ApplyTheme(newColor);
    }

    public class QueueItemRef
    {
        public string Playlist { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public int? StartTime { get; set; }
        public int? EndTime { get; set; }
    }
}