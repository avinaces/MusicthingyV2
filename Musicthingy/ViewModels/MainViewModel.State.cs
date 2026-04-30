using System.Diagnostics;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Musicthingy.Models;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const string LibraryFileName = "library.json";
    private CancellationTokenSource? _saveLibraryCts;

    private bool _isRestoringState = true;
    private double _initialRestorePosition = 0;

    /// <summary>
    /// Call this whenever Playlists or Tracks are added, edited, or removed.
    /// It waits 1 second before saving, preventing lag if multiple edits happen at once.
    /// </summary>
    public void RequestSaveLibrary()
    {
        _saveLibraryCts?.Cancel();
        _saveLibraryCts = new CancellationTokenSource();
        var token = _saveLibraryCts.Token;

        Task.Delay(1000, token).ContinueWith(async t =>
        {
            if (!t.IsCanceled)
            {
                await SaveLibraryAsync();
            }
        }, TaskScheduler.Default);
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
                iconType = p.IconType,
                source = p.Source,
                createdAt = p.CreatedAt,
                updatedAt = p.UpdatedAt,
                tags = p.Tags.ToList(),
                items = p.Items.Select(i => new PlaylistItemDto
                {
                    title = i.Title,
                    url = i.Url,
                    startTime = i.StartTime,
                    endTime = i.EndTime,
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
                            IconType = pDto.iconType ?? "",
                            Source = pDto.source ?? "",
                            CreatedAt = pDto.createdAt ?? DateTime.Now,
                            UpdatedAt = pDto.updatedAt ?? DateTime.Now,
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
#if DEBUG
            LoadDummyData();
#endif
            _isRestoringState = false;
        });
    }

    private void RestorePlaybackState()
    {
        string? selectedPlaylistName = Preferences.Default.Get("SelectedPlaylistName", string.Empty);
        string? currentTrackUrl = Preferences.Default.Get("CurrentTrackUrl", string.Empty);
        string? queueJson = Preferences.Default.Get("PlaybackQueueUrls", string.Empty);

        // Grab the position we were at before app close
        _initialRestorePosition = Preferences.Default.Get("CurrentPositionSeconds", 0.0);

        if (!string.IsNullOrEmpty(selectedPlaylistName))
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Name == selectedPlaylistName);
        }

        if (SelectedPlaylist != null)
        {
            // 1. Restore the PlaybackQueue BEFORE setting the CurrentTrack.
            // This prevents OnCurrentTrackChanged from thinking the queue is empty and destroying the shuffle.
            if (!string.IsNullOrEmpty(queueJson))
            {
                try
                {
                    var queueUrls = JsonSerializer.Deserialize<List<string>>(queueJson);
                    if (queueUrls != null && queueUrls.Count > 0)
                    {
                        PlaybackQueue.Clear();
                        foreach (var url in queueUrls)
                        {
                            var track = SelectedPlaylist.Items.FirstOrDefault(i => i.Url == url);
                            if (track != null)
                            {
                                PlaybackQueue.Add(track);
                            }
                        }
                    }
                }
                catch { /* Ignore parsing errors from old/bad cache */ }
            }

            // 2. Now restore the CurrentTrack.
            if (!string.IsNullOrEmpty(currentTrackUrl))
            {
                var track = SelectedPlaylist.Items.FirstOrDefault(i => i.Url == currentTrackUrl);
                if (track != null)
                {
                    CurrentTrack = track;
                }
            }
        }

        _isRestoringState = false;
    }
}