using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    // --- TIME TRACKING & ENFORCING END TIME ---
    [ObservableProperty]
    public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    private PlaylistItem? _lastTrackSkipped;
    private bool _hasPerformedInitialSeek;
    private DateTime _lastPositionSave = DateTime.MinValue;

    // --- THE BACKGROUND PLAYBACK QUEUE ---
    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    private void SavePlaybackQueueState()
    {
        if (_isRestoringState) return;

        // Save the queue order as a lightweight JSON array of URLs
        var queueUrls = PlaybackQueue.Select(t => t.Url).ToList();
        Preferences.Default.Set("PlaybackQueueUrls", JsonSerializer.Serialize(queueUrls));
    }
    [RelayCommand]
    private void PlayPlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        PlaybackQueue.Clear();
        foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);

        SavePlaybackQueueState(); // Remember the queue order
        CurrentTrack = PlaybackQueue[0];
    }

    [RelayCommand]
    private void ShufflePlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        var shuffledItems = SelectedPlaylist.Items.OrderBy(x => Random.Shared.Next()).ToList();

        PlaybackQueue.Clear();
        foreach (var item in shuffledItems) PlaybackQueue.Add(item);

        SavePlaybackQueueState(); // Remember the shuffled queue order
        CurrentTrack = PlaybackQueue[0];
    }

    [RelayCommand]
    public void PlayNext()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        CurrentTrack = PlaybackQueue[nextIndex];
    }

    [RelayCommand]
    public void PlayPrevious()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = PlaybackQueue.Count - 1;
        CurrentTrack = PlaybackQueue[prevIndex];
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        if (!_hasPerformedInitialSeek && CurrentTrack != null && value.TotalSeconds > 0)
        {
            _hasPerformedInitialSeek = true;
            double startPos = CurrentTrack.StartTime ?? 0;
            string action = "StartPlayback"; // By default, actively start playing

            if (_initialRestorePosition > 0)
            {
                // We are restoring exactly where the user left off
                startPos = _initialRestorePosition;
                _initialRestorePosition = 0; // Consume the value

                // Change action so the view knows to stay paused
                action = "RestorePlayback";
            }
            else
            {
#if WINDOWS
                startPos = 0; // Windows slices the file, so it starts at 0
#endif
            }

            WeakReferenceMessenger.Default.Send(new MediaControlMessage
            {
                Action = action,
                Position = TimeSpan.FromSeconds(startPos)
            });
        }
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        // Save the position to preferences every 5 seconds without freezing the UI
        if (!_isRestoringState && value.TotalSeconds > 0 && (DateTime.Now - _lastPositionSave).TotalSeconds >= 5)
        {
            Preferences.Default.Set("CurrentPositionSeconds", value.TotalSeconds);
            _lastPositionSave = DateTime.Now;
        }

        if (CurrentTrack?.EndTime.HasValue == true && _lastTrackSkipped != CurrentTrack)
        {
            double endTimeCheck = CurrentTrack.EndTime.Value;

#if WINDOWS
            endTimeCheck = CurrentTrack.EndTime.Value - (CurrentTrack.StartTime ?? 0);
#endif

            if (value.TotalSeconds >= endTimeCheck)
            {
                _lastTrackSkipped = CurrentTrack;
                MainThread.BeginInvokeOnMainThread(() => PlayNextCommand.Execute(null));
            }
        }
    }

    partial void OnCurrentTrackChanged(PlaylistItem? oldValue, PlaylistItem? newValue)
    {
        _lastTrackSkipped = null;
        _hasPerformedInitialSeek = false;

        // Check if we are restoring state OR if the new value is null (app is cleaning up)
        // We only want to save to Preferences if a USER actively changed the track.
        if (!_isRestoringState && newValue != null)
        {
            Preferences.Default.Set("CurrentTrackUrl", newValue.Url ?? string.Empty);
            Preferences.Default.Set("CurrentPositionSeconds", 0.0);
        }
        // If newValue is null, we usually don't want to wipe the "Last Played" 
        // preference immediately, because it might be the "Cleanup" logic firing.

        if (newValue == null)
        {
            CurrentMediaSource = null;
            // Only send stop if this is the active instance
            return;
        }

        if (SelectedPlaylist != null && !PlaybackQueue.Contains(newValue))
        {
            PlaybackQueue.Clear();
            foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);
            SavePlaybackQueueState(); // Remember if it rebuilt sequentially
            _ = WarmPlaylistCacheAsync();
        }

        Debug.WriteLine("Last played at: " + newValue?.LastPlayedAt);
        if (newValue != null) newValue.LastPlayedAt = DateTime.Now;

        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;

#if ANDROID
        if (oldValue != null && oldValue.Url == newValue.Url && CurrentMediaSource != null)
        {
            double newStart = newValue.StartTime ?? 0;
            bool isContinuous = Math.Abs(CurrentPosition.TotalSeconds - newStart) <= 10;

            _hasPerformedInitialSeek = true;
            if (!isContinuous)
            {
                WeakReferenceMessenger.Default.Send(new MediaControlMessage
                {
                    Action = "StartPlayback",
                    Position = TimeSpan.FromSeconds(newStart)
                });
            }
            _ = PrefetchNextTrackAsync();
            return;
        }
#endif

        WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });
        CurrentMediaSource = null;

        _ = ReloadMediaAsync(newValue);
        _ = PrefetchNextTrackAsync();
    }
}