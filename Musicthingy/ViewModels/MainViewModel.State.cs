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
    private double _initialRestorePosition = 0;

    // NEW SAFETY FLAG
    private bool _isLibraryLoadFailed = false;

    private static PlaylistIconType ParseIconType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return PlaylistIconType.Playlist;
        raw = raw.Trim();
        if (raw.Equals("playlist", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Playlist;
        if (raw.Equals("track", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Track;
        if (raw.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.YouTube;
        if (Enum.TryParse<PlaylistIconType>(raw, true, out var parsed)) return parsed;
        return PlaylistIconType.Playlist;
    }

    public void RequestSaveLibrary()
    {
        // CRITICAL SAFETY CHECK: Do not attempt to save if the library failed to load!
        if (_isLibraryLoadFailed) return;

        _saveLibraryCts?.Cancel();
        _saveLibraryCts = new CancellationTokenSource();
        var token = _saveLibraryCts.Token;

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
        catch { return; }

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
        if (_isLibraryLoadFailed) return; // Redundant safety net

        Debug.WriteLine("Saving Library...");
        try
        {
            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            string tempPath = path + ".tmp";

            string json = JsonSerializer.Serialize(libraryData);
            await File.WriteAllTextAsync(tempPath, json);

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save library: {ex.Message}");
        }
    }

    private async Task SaveLibraryAsync()
    {
        if (_isLibraryLoadFailed) return; // Redundant safety net

        Debug.WriteLine("Saving Library...");
        try
        {
            var library = Playlists.Select(p => new PlaylistDto
            {
                // ... mapping logic remains unchanged
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
        SettingsManager.IsRestoringState = true;
        _isLibraryLoadFailed = false; // Reset on startup

        try
        {
            _settingsManager.LoadAll();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex}");
        }

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

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            Playlists.Clear();
                            foreach (var lp in loadedPlaylists) Playlists.Add(lp);
                            RestorePlaybackState();
                        }
                        finally
                        {
                            SettingsManager.IsRestoringState = false;
                        }
                    });

                    return; // Successfully loaded
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load library state: {ex.Message}");

            // 1. SET THE LOCK FLAG
            _isLibraryLoadFailed = true;

            // 2. ALERT THE USER ON THE MAIN THREAD
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                SettingsManager.IsRestoringState = false;
                await ShowConfirmAsync(
                    "Library Load Failed",
                    $"There was an error loading your library data. To protect your playlists from being accidentally deleted, all saving has been temporarily disabled.\n\nError: {ex.Message}",
                    "Understood",
                    ModalBackgroundTapBehavior.Ignore,
                    isDanger: true);
            });

            return;
        }

        // Fallback unlock if library doesn't exist (first launch) or is empty
        MainThread.BeginInvokeOnMainThread(() => SettingsManager.IsRestoringState = false);
    }

    private void RestorePlaybackState()
    {
        IsShuffleEnabled = _settingsManager.ShuffleEnabled.Value;
        RepeatState = (RepeatMode)_settingsManager.RepeatMode.Value;

        string? selectedPlaylistName = _settingsManager.SelectedPlaylistName.Value;
        string? queueJson = _settingsManager.PlaybackQueueData.Value;
        string? currentTrackJson = _settingsManager.CurrentTrackData.Value;

        _initialRestorePosition = _settingsManager.CurrentPositionSeconds.Value;

        if (!string.IsNullOrEmpty(selectedPlaylistName))
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Name == selectedPlaylistName);
        }

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
                        var track = targetPlaylist?.Items.FirstOrDefault(i =>
                            i.Url == qRef.Url &&
                            i.StartTime == qRef.StartTime &&
                            i.EndTime == qRef.EndTime);

                        if (track != null) PlaybackQueue.Add(track);
                    }
                }
            }
            catch { }
        }

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

                    if (track != null) CurrentTrack = track;
                }
            }
            catch { }
        }
    }

    public void LoadThemeState()
    {
        Color seedColor;
        Color? androidSystemColor = SystemThemeHelper.GetAndroidSystemAccentColor();

        if (androidSystemColor != null && _settingsManager.UseSystemTheme.Value) seedColor = androidSystemColor;
        else
        {
            string savedHex = _settingsManager.ThemeSeedColor.Value;
            seedColor = Color.FromArgb(savedHex);
        }

        _themeManager.ApplyTheme(seedColor);
    }

    public void SaveThemeColor(Color newColor)
    {
        string hex = newColor.ToHex();
        _settingsManager.ThemeSeedColor.Value = hex;
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