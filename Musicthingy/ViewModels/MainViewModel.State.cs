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
    private bool _isLibraryLoadFailed = false;

    // Single central setup for JSON serialization
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public void RequestSaveLibrary()
    {
        if (_isLibraryLoadFailed) return;

        _saveLibraryCts?.Cancel();
        _saveLibraryCts = new CancellationTokenSource();
        var token = _saveLibraryCts.Token;

        // Take a snapshot list of current playlists directly
        List<Playlist> librarySnapshot = Playlists.ToList();

        Task.Delay(1000, token).ContinueWith(async t =>
        {
            if (!t.IsCanceled)
            {
                await SaveLibraryAsync(librarySnapshot);
            }
        }, TaskScheduler.Default);
    }

    private async Task SaveLibraryAsync(List<Playlist> libraryData)
    {
        if (_isLibraryLoadFailed) return;

        Debug.WriteLine("Saving Library...");
        try
        {
            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            string tempPath = path + ".tmp";

            // Magic happens here: No manual mapping needed!
            string json = JsonSerializer.Serialize(libraryData, _jsonOptions);
            await File.WriteAllTextAsync(tempPath, json);

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save library: {ex.Message}");
        }
    }

    public async Task LoadStateAsync()
    {
        SettingsManager.IsRestoringState = true;
        _isLibraryLoadFailed = false;

        try { _settingsManager.LoadAll(); } catch { }

        try
        {
            LoadThemeState();

            string path = Path.Combine(FileSystem.AppDataDirectory, LibraryFileName);
            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path);

                // Directly deserialize into the Models!
                var loadedPlaylists = JsonSerializer.Deserialize<List<Playlist>>(json, _jsonOptions);

                if (loadedPlaylists != null && loadedPlaylists.Count > 0)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            Playlists.Clear();
                            foreach (var lp in loadedPlaylists)
                            {
                                // Because Playlist is ignored on the child to prevent a circular loop, we assign it here once
                                foreach (var item in lp.Items) item.Playlist = lp;
                                Playlists.Add(lp);
                            }
                            RestorePlaybackState();
                        }
                        finally
                        {
                            SettingsManager.IsRestoringState = false;
                        }
                    });

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _isLibraryLoadFailed = true;
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