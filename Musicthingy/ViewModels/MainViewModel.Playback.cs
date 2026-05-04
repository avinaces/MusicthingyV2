using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] public partial TimeSpan CurrentPosition { get; set; }
    [ObservableProperty] public partial TimeSpan Duration { get; set; }
    [ObservableProperty] public partial double CurrentPositionSeconds { get; set; }
    [ObservableProperty] public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    public partial bool IsPlaying { get; set; }

    public string PlayPauseIcon => IsPlaying ? "pause_icon.png" : "play_icon.png";

    private PlaylistItem? _lastTrackSkipped;
    private DateTime _lastPositionSave = DateTime.MinValue;
    private bool _isDragging;

    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    private void SavePlaybackQueueState()
    {
        if (_isRestoringState) return;
        var queueUrls = PlaybackQueue.Select(t => t.Url).ToList();
        Preferences.Default.Set("PlaybackQueueUrls", JsonSerializer.Serialize(queueUrls));
    }
    [RelayCommand]
    private void PlayPlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        PlaybackQueue.Clear();
        foreach (var item in SelectedPlaylist.Items) PlaybackQueue.Add(item);

        SavePlaybackQueueState();
        CurrentTrack = PlaybackQueue[0];
    }

    [RelayCommand]
    private void ShufflePlaylist()
    {
        if (SelectedPlaylist == null || SelectedPlaylist.Items.Count == 0) return;

        var shuffledItems = SelectedPlaylist.Items.OrderBy(x => Random.Shared.Next()).ToList();
        PlaybackQueue.Clear();
        foreach (var item in shuffledItems) PlaybackQueue.Add(item);

        SavePlaybackQueueState();
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

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player == null) return;

        if (IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            AcquireTransientWakeLock(); // Keep awake while network reconnects/buffers
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
            // Convert the slider's relative time back to absolute VLC time
            double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
            _player.Time = (long)((CurrentPositionSeconds * 1000.0) + startMs);
        }
        _isDragging = false;

        // Notify the OS widget so its seekbar updates seamlessly
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
            Preferences.Default.Set("CurrentTrackUrl", newValue.Url ?? string.Empty);
            Preferences.Default.Set("CurrentPositionSeconds", 0.0);
        }

        if (newValue == null)
        {
            _player?.Stop();
            return;
        }

        // FIX: Look at the track's native Playlist, not SelectedPlaylist
        if (newValue.Playlist != null && !PlaybackQueue.Contains(newValue))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PlaybackQueue.Clear();
                foreach (var item in newValue.Playlist.Items) PlaybackQueue.Add(item);
                SavePlaybackQueueState();
            });
        }

        newValue.LastPlayedAt = DateTime.Now;

        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;

        _ = ReloadMediaAsync(newValue);
    }
}