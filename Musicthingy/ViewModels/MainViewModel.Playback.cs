using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
    [ObservableProperty] public partial double CurrentPositionSeconds { get; set; } = 0;
    [ObservableProperty] public partial double DurationSeconds { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIconSource))]
    [NotifyPropertyChangedFor(nameof(RepeatIconColor))]
    public partial RepeatMode RepeatState { get; set; } = RepeatMode.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleIconColor))]
    [NotifyPropertyChangedFor(nameof(ShuffleIconSource))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoFollowIconColor))]
    [NotifyPropertyChangedFor(nameof(AutoFollowIconSource))]
    public partial bool IsAutoFollowEnabled { get; set; }

    public string ShuffleIconSource =>
    IsShuffleEnabled
        ? "shuffle_icon.png"
        : "shuffle_off_icon.png";

    public string AutoFollowIconSource =>
        IsAutoFollowEnabled
            ? "locate_fixed_icon.png"
            : "locate_icon.png";

    public Color AutoFollowIconColor => IsAutoFollowEnabled ? ThemeResources.Color("IconSelectedColor") : ThemeResources.Color("MutedColor");

    public string PlayPauseIcon => IsPlaying ? "pause_icon.png" : "play_icon.png";

    public Color ShuffleIconColor => IsShuffleEnabled ? ThemeResources.Color("IconSelectedColor") : ThemeResources.Color("MutedColor");

    public string RepeatIconSource => RepeatState switch
    {
        RepeatMode.All => "repeat_icon.png",
        RepeatMode.One => "repeat_1_icon.png",
        _ => "repeat_off_icon.png"
    };

    public Color RepeatIconColor => RepeatState switch
    {
        RepeatMode.All => ThemeResources.Color("IconSelectedColor"),
        RepeatMode.One => ThemeResources.Color("IconSelectedColor"),
        _ => ThemeResources.Color("MutedColor")
    };

    private PlaylistItem? _lastTrackSkipped;
    private DateTime _lastPositionSave = DateTime.MinValue;
    private bool _isDragging;

    // A shadow queue to preserve the un-shuffled state (including any manually added tracks)
    private List<PlaylistItem> _unshuffledQueue = new();

    [ObservableProperty]
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
                // Ensure shadow queue is populated as a fallback for app reboots
                if (_unshuffledQueue.Count == 0) _unshuffledQueue = PlaybackQueue.ToList();

                var remaining = PlaybackQueue.Where(x => x != CurrentTrack).OrderBy(x => Random.Shared.Next()).ToList();
                PlaybackQueue.Clear();
                PlaybackQueue.Add(CurrentTrack);
                foreach (var item in remaining) PlaybackQueue.Add(item);
            }
            else
            {
                if (_unshuffledQueue.Count == 0)
                {
                    // Fallback if _unshuffledQueue wasn't tracked across an app restart
                    if (CurrentTrack.Playlist != null)
                    {
                        var sourceList = (CurrentTrack.Playlist == SelectedPlaylist) ? DisplayedItems : CurrentTrack.Playlist.Items;
                        _unshuffledQueue = sourceList.ToList();
                    }
                    else
                    {
                        _unshuffledQueue = PlaybackQueue.ToList();
                    }
                }

                PlaybackQueue.Clear();
                // Restore from the shadow queue, which contains all context tracks AND manually added tracks
                foreach (var item in _unshuffledQueue) PlaybackQueue.Add(item);
            }
            SavePlaybackQueueState();
        });
    }

    [RelayCommand]
    private void ToggleRepeat()
    {
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
        if (SelectedPlaylist == null || DisplayedItems.Count == 0) return;

        IsShuffleEnabled = false;
        Preferences.Default.Set("IsShuffleEnabled", false);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _unshuffledQueue = DisplayedItems.ToList();
            PlaybackQueue.Clear();
            foreach (var item in _unshuffledQueue) PlaybackQueue.Add(item);
            SavePlaybackQueueState();
            CurrentTrack = PlaybackQueue[0];
        });
    }

    [RelayCommand]
    private void ShufflePlaylist()
    {
        if (SelectedPlaylist == null || DisplayedItems.Count == 0) return;

        IsShuffleEnabled = true;
        Preferences.Default.Set("IsShuffleEnabled", true);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _unshuffledQueue = DisplayedItems.ToList();
            var shuffledItems = _unshuffledQueue.OrderBy(x => Random.Shared.Next()).ToList();
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

        if (trigger == "Natural" && RepeatState == RepeatMode.One)
        {
            CurrentTrack.LastPlayedAt = DateTime.Now;
            _ = ReloadMediaAsync(CurrentTrack);
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
        if (IsLoadingMedia) return;

        if (IsPlaying)
        {
            _playbackService.Pause();
        }
        else
        {
            AcquireTransientWakeLock();
            _playbackService.Play();
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
        double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
        _playbackService.Seek((CurrentPositionSeconds * 1000.0) + startMs);

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
            ActiveTimedLyrics.Clear();
            HasNoLyrics = true;

            _playbackService.Stop();

            return;
        }

        ParseLyrics(newValue.Lyrics);

        if (newValue.Playlist != null && !PlaybackQueue.Contains(newValue))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PlaybackQueue.Clear();
                var sourceList = (newValue.Playlist == SelectedPlaylist) ? DisplayedItems.ToList() : newValue.Playlist.Items.ToList();

                // Track context reset
                _unshuffledQueue = sourceList;

                if (IsShuffleEnabled)
                {
                    var remaining = _unshuffledQueue.Where(x => x != newValue).OrderBy(x => Random.Shared.Next()).ToList();
                    PlaybackQueue.Add(newValue);
                    foreach (var item in remaining) PlaybackQueue.Add(item);
                }
                else
                {
                    foreach (var item in _unshuffledQueue) PlaybackQueue.Add(item);
                }
                SavePlaybackQueueState();
            });
        }

        newValue.LastPlayedAt = DateTime.Now;

        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;

        _ = ReloadMediaAsync(newValue);
    }

    [RelayCommand]
    private void ToggleAutoFollow()
    {
        IsAutoFollowEnabled = !IsAutoFollowEnabled;
        if (IsAutoFollowEnabled)
        {
            TriggerAutoFollow();
        }
    }

    public void TriggerAutoFollow()
    {
        if (CurrentTrack != null && CurrentTrack.Playlist != null)
        {
            if (SelectedPlaylist != CurrentTrack.Playlist)
            {
                SelectedPlaylist = CurrentTrack.Playlist;
            }

            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ScrollToTrackMessage(CurrentTrack));
        }
    }

    // --- AddToPlaybackQueue Overloads ---

    public void AddToPlaybackQueue(PlaylistItem item) => AddToPlaybackQueue(new[] { item });
    public void AddToPlaybackQueue(Playlist playlist) => AddToPlaybackQueue(playlist.Items);

    public void AddToPlaybackQueue(IEnumerable<PlaylistItem> items)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            bool isModified = false;

            // Failsafe for app restarts
            if (_unshuffledQueue.Count == 0 && PlaybackQueue.Count > 0)
                _unshuffledQueue = PlaybackQueue.ToList();

            // Determine where we currently are in the queue
            int currentIndex = CurrentTrack != null ? PlaybackQueue.IndexOf(CurrentTrack) : -1;

            foreach (PlaylistItem item in items)
            {
                if (item.Playlist == null) continue;

                if (!PlaybackQueue.Contains(item))
                {
                    if (IsShuffleEnabled && currentIndex != -1)
                    {
                        // Distribute the new track randomly ANYWHERE after the current playing track
                        // +1 allows it to potentially be placed at the very end as well
                        int insertIndex = Random.Shared.Next(currentIndex + 1, PlaybackQueue.Count + 1);
                        PlaybackQueue.Insert(insertIndex, item);
                    }
                    else
                    {
                        // If shuffle is off (or nothing is playing), just add to the end
                        PlaybackQueue.Add(item);
                    }

                    // Always append to our shadow queue so it survives un-shuffling in standard order
                    if (!_unshuffledQueue.Contains(item))
                    {
                        _unshuffledQueue.Add(item);
                    }

                    isModified = true;
                }
            }

            if (isModified)
            {
                SavePlaybackQueueState();
            }
        });
    }

    // --- PlayNextInQueue Overloads ---

    public void PlayNextInQueue(PlaylistItem item) => PlayNextInQueue(new[] { item });
    public void PlayNextInQueue(Playlist playlist) => PlayNextInQueue(playlist.Items);

    public void PlayNextInQueue(IEnumerable<PlaylistItem> items)
    {
        if (CurrentTrack == null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            bool isModified = false;
            int insertOffset = 1;

            if (_unshuffledQueue.Count == 0 && PlaybackQueue.Count > 0) _unshuffledQueue = PlaybackQueue.ToList();

            foreach (PlaylistItem item in items)
            {
                if (item.Playlist == null || item == CurrentTrack) continue;

                PlaybackQueue.Remove(item);
                _unshuffledQueue.Remove(item);

                int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
                if (currentIndex == -1) break;

                PlaybackQueue.Insert(currentIndex + insertOffset, item);

                // Track into unshuffled queue right after the current track so it plays naturally un-shuffled too.
                int unIndex = _unshuffledQueue.IndexOf(CurrentTrack);
                if (unIndex != -1)
                {
                    _unshuffledQueue.Insert(unIndex + insertOffset, item);
                }
                else
                {
                    _unshuffledQueue.Add(item);
                }

                insertOffset++;
                isModified = true;
            }

            if (isModified)
            {
                SavePlaybackQueueState();
            }
        });
    }
}