using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public enum RepeatMode
{
    Off = 0,
    All = 1,
    One = 2
}

public partial class MainViewModel
{
    [ObservableProperty] public partial TimeSpan CurrentPosition { get; set; }
    [ObservableProperty] public partial TimeSpan Duration { get; set; }
    [ObservableProperty] public partial double CurrentPositionSeconds { get; set; }
    [ObservableProperty] public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleIconColor))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIconSource))]
    [NotifyPropertyChangedFor(nameof(RepeatIconColor))]
    public partial RepeatMode RepeatState { get; set; } = RepeatMode.Off;

    public string PlayPauseIcon => IsPlaying ? "pause_icon.png" : "play_icon.png";

    // Gray when off, Soft Pastel Blue when active
    public Color ShuffleIconColor => IsShuffleEnabled ? Color.FromArgb("#adcff0") : Color.FromArgb("#8b949e");

    // Dynamic Icon Images
    public string RepeatIconSource => RepeatState switch
    {
        RepeatMode.All => "repeat_icon.png",
        RepeatMode.One => "repeat_1_icon.png",
        _ => "repeat_off_icon.png"
    };

    // Gray when off, Soft Pastel Blue when repeating All or One
    public Color RepeatIconColor => RepeatState switch
    {
        RepeatMode.All => Color.FromArgb("#adcff0"),
        RepeatMode.One => Color.FromArgb("#adcff0"),
        _ => Color.FromArgb("#8b949e")
    };

    private PlaylistItem? _lastTrackSkipped;
    private DateTime _lastPositionSave = DateTime.MinValue;
    private bool _isDragging; [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    private void SavePlaybackQueueState()
    {
        if (_isRestoringState) return;

        var queueData = PlaybackQueue
            .Where(t => t.Playlist != null)
            .Select(t => new QueueItemRef
            {
                Playlist = t.Playlist!.Name,
                Url = t.Url,
                StartTime = t.StartTime,
                EndTime = t.EndTime
            })
            .ToList();

        Preferences.Default.Set("PlaybackQueueData", JsonSerializer.Serialize(queueData));
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        IsShuffleEnabled = !IsShuffleEnabled;
        Preferences.Default.Set("IsShuffleEnabled", IsShuffleEnabled);

        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (IsShuffleEnabled)
            {
                var remaining = PlaybackQueue.Where(x => x != CurrentTrack).OrderBy(x => Random.Shared.Next()).ToList();
                PlaybackQueue.Clear();
                PlaybackQueue.Add(CurrentTrack);
                foreach (var item in remaining) PlaybackQueue.Add(item);
            }
            else
            {
                if (CurrentTrack.Playlist != null)
                {
                    PlaybackQueue.Clear();
                    foreach (var item in CurrentTrack.Playlist.Items) PlaybackQueue.Add(item);
                }
            }
            SavePlaybackQueueState();
        });
    }

    [RelayCommand]
    private void ToggleRepeat()
    {
        // Cycle through: Off -> All -> One -> Off
        RepeatState = RepeatState switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };

        Preferences.Default.Set("RepeatState", (int)RepeatState);
    }

    [RelayCommand]
    private void PlayPlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        IsShuffleEnabled = false;
        Preferences.Default.Set("IsShuffleEnabled", false);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            PlaybackQueue.Clear();
            foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);
            SavePlaybackQueueState();
            CurrentTrack = PlaybackQueue[0];
        });
    }
    [RelayCommand]
    private void ShufflePlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        IsShuffleEnabled = true;
        Preferences.Default.Set("IsShuffleEnabled", true);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var shuffledItems = SelectedPlaylist.Items.OrderBy(x => Random.Shared.Next()).ToList();
            PlaybackQueue.Clear();
            foreach (var item in shuffledItems) PlaybackQueue.Add(item);
            SavePlaybackQueueState();
            CurrentTrack = PlaybackQueue[0];
        });
    }

    [RelayCommand]
    public void PlayNext(string? trigger = null)
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        // If the track ended naturally and we are on "Repeat One", seamlessly restart it
        if (trigger == "Natural" && RepeatState == RepeatMode.One)
        {
            CurrentTrack.LastPlayedAt = DateTime.Now;
            _ = ReloadMediaAsync(CurrentTrack); // Re-feed VLC instantly
            return;
        }

        int nextIndex = currentIndex + 1;
        if (nextIndex >= PlaybackQueue.Count)
        {
            if (RepeatState == RepeatMode.All || RepeatState == RepeatMode.One)
            {
                if (IsShuffleEnabled)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var shuffled = PlaybackQueue.OrderBy(x => Random.Shared.Next()).ToList();
                        PlaybackQueue.Clear();
                        foreach (var item in shuffled) PlaybackQueue.Add(item);
                        SavePlaybackQueueState();
                        CurrentTrack = PlaybackQueue[0];
                    });
                    return;
                }
                else
                {
                    nextIndex = 0;
                }
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => CurrentTrack = null);
                return;
            }
        }

        CurrentTrack = PlaybackQueue[nextIndex];
    }

    [RelayCommand]
    public void PlayPrevious()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        if (CurrentPosition.TotalSeconds > 3)
        {
            CurrentPositionSeconds = 0;
            SeekCommand.Execute(null);
            return;
        }

        int prevIndex = currentIndex - 1;
        if (prevIndex < 0)
        {
            if (RepeatState == RepeatMode.All || RepeatState == RepeatMode.One)
            {
                prevIndex = PlaybackQueue.Count - 1;
            }
            else
            {
                CurrentPositionSeconds = 0;
                SeekCommand.Execute(null);
                return;
            }
        }

        CurrentTrack = PlaybackQueue[prevIndex];
    }
    [RelayCommand]
    private void TogglePlay()
    {
        if (_player == null || IsLoadingMedia) return;

        if (IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            AcquireTransientWakeLock();
            _player.Play();
        }
    }
    [RelayCommand]
    private void DragStarted()
    {
        _isDragging = true;
    }

    [RelayCommand]
    private void Seek()
    {
        if (_player != null && _player.IsSeekable)
        {
            double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
            _player.Time = (long)((CurrentPositionSeconds * 1000.0) + startMs);
        }
        _isDragging = false;
        SyncOSPlaybackState();
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        if (!_isRestoringState && value.TotalSeconds > 0 && (DateTime.Now - _lastPositionSave).TotalSeconds >= 5)
        {
            Preferences.Default.Set("CurrentPositionSeconds", value.TotalSeconds);
            _lastPositionSave = DateTime.Now;
        }
    }

    partial void OnCurrentTrackChanged(PlaylistItem? oldValue, PlaylistItem? newValue)
    {
        _lastTrackSkipped = null;

        if (!_isRestoringState && newValue != null)
        {
            var currentRef = new QueueItemRef
            {
                Playlist = newValue.Playlist?.Name ?? string.Empty,
                Url = newValue.Url ?? string.Empty,
                StartTime = newValue.StartTime,
                EndTime = newValue.EndTime
            };

            Preferences.Default.Set("CurrentTrackData", JsonSerializer.Serialize(currentRef));
            Preferences.Default.Set("CurrentPositionSeconds", 0.0);
        }

        if (newValue == null)
        {
            _player?.Stop();
            return;
        }

        if (newValue.Playlist != null && !PlaybackQueue.Contains(newValue))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PlaybackQueue.Clear();
                if (IsShuffleEnabled)
                {
                    var remaining = newValue.Playlist.Items.Where(x => x != newValue).OrderBy(x => Random.Shared.Next()).ToList();
                    PlaybackQueue.Add(newValue);
                    foreach (var item in remaining) PlaybackQueue.Add(item);
                }
                else
                {
                    foreach (var item in newValue.Playlist.Items) PlaybackQueue.Add(item);
                }
                SavePlaybackQueueState();
            });
        }

        newValue.LastPlayedAt = DateTime.Now;

        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;

        _ = ReloadMediaAsync(newValue);
    }
}