using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;

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

    // --- THE BACKGROUND PLAYBACK QUEUE ---
    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    [RelayCommand]
    private void PlayPlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        PlaybackQueue.Clear();
        foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);

        CurrentTrack = PlaybackQueue[0];
    }

    [RelayCommand]
    private void ShufflePlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        var shuffledItems = SelectedPlaylist.Items.OrderBy(x => Random.Shared.Next()).ToList();

        PlaybackQueue.Clear();
        foreach (var item in shuffledItems) PlaybackQueue.Add(item);

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

#if WINDOWS
            startPos = 0; // Windows slices the file, so it starts at 0
#endif

            WeakReferenceMessenger.Default.Send(new MediaControlMessage
            {
                Action = "StartPlayback",
                Position = TimeSpan.FromSeconds(startPos)
            });
        }
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
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

        if (newValue == null)
        {
            CurrentMediaSource = null;
            WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });
            return;
        }

        if (SelectedPlaylist != null && !PlaybackQueue.Contains(newValue))
        {
            PlaybackQueue.Clear();
            foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);
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