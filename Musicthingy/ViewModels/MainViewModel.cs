using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Converter;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        _dataService = new PlaylistDataService();
#if DEBUG
        LoadDummyData();
#endif
#if WINDOWS
        // Clean up old cached video files in the background on startup
        Task.Run(CleanupCache);
#endif
        // Listen for OS Media Key presses (Next/Previous) from Windows and Android
        WeakReferenceMessenger.Default.Register<Models.MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Next" && PlayNextCommand.CanExecute(null))
                PlayNextCommand.Execute(null);
            else if (m.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
                PlayPreviousCommand.Execute(null);
        });
    }

    private void CleanupCache()
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            var files = Directory.GetFiles(cacheDir, "*.mp4");
            foreach (var file in files)
            {
                // Delete cache files older than 3 days to save disk space
                if (File.GetCreationTime(file) < DateTime.Now.AddDays(-3))
                {
                    File.Delete(file);
                }
            }
        }
        catch { }
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

    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _resolvedCache = new();
    private readonly PlaylistDataService _dataService;

    [ObservableProperty]
    public partial bool IsLoadingMedia { get; set; }

    [ObservableProperty]
    public partial double MediaDownloadProgress { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new();

    [ObservableProperty]
    public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotMediaSelected))]
    public partial PlaylistItem? CurrentTrack { get; set; }

    public bool IsMediaSelected => CurrentTrack != null;
    public bool IsNotMediaSelected => CurrentTrack == null;

    [ObservableProperty]
    public partial MediaSource? CurrentMediaSource { get; set; }

    // --- TIME TRACKING & ENFORCING END TIME ---
    [ObservableProperty]
    public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    private PlaylistItem? _lastTrackSkipped;
    private bool _hasPerformedInitialSeek;

    partial void OnDurationChanged(TimeSpan value)
    {
        if (!_hasPerformedInitialSeek && CurrentTrack != null && value.TotalSeconds > 0)
        {
            _hasPerformedInitialSeek = true;

            double startPos = CurrentTrack.StartTime ?? 0;

#if WINDOWS
            // Since Windows physically sliced the local file to exactly fit the track,
            // the file's internal timeline naturally starts at 0.
            startPos = 0;
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
            // On Windows, the file is sliced so its total duration is exactly the length of the track
            endTimeCheck = CurrentTrack.EndTime.Value - (CurrentTrack.StartTime ?? 0);
#endif

            if (value.TotalSeconds >= endTimeCheck)
            {
                _lastTrackSkipped = CurrentTrack;
                MainThread.BeginInvokeOnMainThread(() => PlayNextCommand.Execute(null));
            }
        }
    }

    // --- THE BACKGROUND PLAYBACK QUEUE ---
    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    private PlaylistItem? _prefetchedTrack;
    private MediaSource? _prefetchedMediaSource;

    [RelayCommand]
    public void PlayNext()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        CurrentTrack = PlaybackQueue[nextIndex];
    }

    private async Task WarmPlaylistCacheAsync()
    {
#if ANDROID
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        await Parallel.ForEachAsync(PlaybackQueue, options, async (item, token) =>
        {
            await ResolveMediaSourceAsync(item);
        });
#else
        // Let Windows handle downloading on-the-fly via PrefetchNextTrackAsync 
        // to avoid mass-spawning FFmpeg processes.
        await Task.CompletedTask;
#endif
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
            foreach (var item in SelectedPlaylist.Items)
                PlaybackQueue.Add(item);

            _ = WarmPlaylistCacheAsync();
        }

#if ANDROID
        // OPTIMIZATION: Same-URL Seamless Transition 
        // (Android uses single streaming URL, Windows uses individual sliced files now)
        if (oldValue != null && oldValue.Url == newValue.Url && CurrentMediaSource != null)
        {
            double newStart = newValue.StartTime ?? 0;
            bool isContinuous = Math.Abs(CurrentPosition.TotalSeconds - newStart) <= 10;

            if (isContinuous)
            {
                _hasPerformedInitialSeek = true;
            }
            else
            {
                _hasPerformedInitialSeek = true;
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

    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        await Task.Delay(50);
        if (CurrentTrack != newValue) return;

        if (_prefetchedTrack == newValue && _prefetchedMediaSource != null)
        {
            CurrentMediaSource = _prefetchedMediaSource;
        }
        else
        {
            IsLoadingMedia = true;
            MediaDownloadProgress = 0;

            var progress = new Progress<double>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() => MediaDownloadProgress = p);
            });

            var resolvedSource = await ResolveMediaSourceAsync(newValue, progress);

            if (CurrentTrack == newValue)
            {
                CurrentMediaSource = resolvedSource;
                IsLoadingMedia = false;
            }
        }
    }

    private async Task PrefetchNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        if (_prefetchedTrack == nextTrack) return;

        _prefetchedTrack = nextTrack;
        _prefetchedMediaSource = await ResolveMediaSourceAsync(nextTrack);
    }

#if WINDOWS
    private async Task<string> GetFFmpegExecutableAsync()
    {
        string cacheFfmpegPath = Path.Combine(FileSystem.CacheDirectory, "ffmpeg.exe");

        // If it's already in the cache directory from a previous run, just return the path
        if (!File.Exists(cacheFfmpegPath))
        {
            // AppContext.BaseDirectory is the safest way to find the app's install folder in .NET
            string installDirFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

            if (File.Exists(installDirFfmpeg))
            {
                // Copy it to the Cache folder where we have full execution permissions
                await Task.Run(() => File.Copy(installDirFfmpeg, cacheFfmpegPath, true));
            }
            else
            {
                throw new FileNotFoundException($"ffmpeg.exe was not found in the build directory: {installDirFfmpeg}");
            }
        }

        return cacheFfmpegPath;
    }
#endif

    private async Task<MediaSource?> ResolveMediaSourceAsync(PlaylistItem track, IProgress<double>? progress = null)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;
        string url = track.Url;

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                var videoId = YoutubeExplode.Videos.VideoId.Parse(url);

#if ANDROID
                if (_resolvedCache.TryGetValue(url, out var cached) && DateTime.Now < cached.Expiry)
                    return MediaSource.FromUri(cached.Url);

                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                var streamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                if (streamInfo != null)
                {
                    _resolvedCache[url] = (streamInfo.Url, DateTime.Now.AddHours(5));
                    return MediaSource.FromUri(streamInfo.Url);
                }

#elif WINDOWS
                string cacheDir = FileSystem.CacheDirectory;
                string tempFilePath = Path.Combine(cacheDir, $"{videoId.Value}_{track.StartTime ?? 0}_{track.EndTime ?? 0}.m4a");

                // If we've already clipped this track, play it instantly
                if (File.Exists(tempFilePath))
                {
                    if (progress != null) progress.Report(1.0);
                    return MediaSource.FromFile(tempFilePath);
                }

                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                
                // THE SECRET FIX TO THE 5 MINUTE HANG:
                // We ask for a Progressive (Muxed) stream instead of a Fragmented Audio stream.
                // FFmpeg can instantly HTTP-seek Progressive streams without downloading the start of the video.
                var progressiveStream = manifest.GetMuxedStreams()
                    .Where(s => s.Container == Container.Mp4)
                    .OrderByDescending(s => s.VideoQuality) // Pick 360p or 720p to keep the download size small
                    .FirstOrDefault();

                string? targetUrl = progressiveStream?.Url;
                
                // Safe fallback to audio-only if for some reason a normal stream isn't available
                if (targetUrl == null) 
                {
                    targetUrl = manifest.GetAudioOnlyStreams().GetWithHighestBitrate()?.Url;
                }

                if (!string.IsNullOrEmpty(targetUrl))
                {
                    string ffmpegPath = await GetFFmpegExecutableAsync();
                    string downloadingFilePath = Path.Combine(cacheDir, $"{videoId.Value}_{Guid.NewGuid()}.temp.m4a");
                    
                    double startPos = track.StartTime ?? 0;
                    
                    // Passing a generic User Agent prevents YouTube from blocking FFmpeg's HTTP requests
                    string args = $"-y -user_agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\" ";
                    args += $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 ";
                    
                    // -ss BEFORE -i makes FFmpeg jump instantly to the timestamp using HTTP Range requests
                    args += $"-ss {startPos} -i \"{targetUrl}\" ";
                    
                    if (track.EndTime.HasValue)
                    {
                        double durationToDownload = track.EndTime.Value - startPos;
                        args += $"-t {durationToDownload} ";
                    }
                    
                    // -vn drops the video part. -c:a copy extracts the pure audio without high CPU usage
                    args += $"-vn -c:a copy -f ipod \"{downloadingFilePath}\"";

                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    // Because this method is now lightning fast (3-8 seconds), we just jump the bar to 50%
                    if (progress != null) progress.Report(0.5);

                    using (var process = System.Diagnostics.Process.Start(processInfo))
                    {
                        if (process != null)
                        {
                            await process.WaitForExitAsync();
                        }
                    }

                    if (File.Exists(downloadingFilePath))
                    {
                        var fileInfo = new FileInfo(downloadingFilePath);
                        // If file is > 0 bytes, the clip was successful
                        if (fileInfo.Length > 0)
                        {
                            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                            File.Move(downloadingFilePath, tempFilePath);
                        }
                        else
                        {
                            File.Delete(downloadingFilePath);
                        }
                    }

                    // Success: Play the local clipped file
                    if (File.Exists(tempFilePath))
                    {
                        if (progress != null) progress.Report(1.0);
                        return MediaSource.FromFile(tempFilePath);
                    }

                    // Ultimate fallback to live stream url if FFmpeg completely fails
                    return MediaSource.FromUri(targetUrl);
                }
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                return null;
            }
            return null;
        }

        // Standard non-YouTube links
        return MediaSource.FromUri(url);
    }


    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}", CreatedAt = DateTime.Now };
        Playlists.Add(newList);
        SelectedPlaylist = newList;
    }

    private static readonly string[] androidFileTypeJSON = ["application/json"];
    private static readonly string[] windowsFileTypeJSON = [".json"];

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, windowsFileTypeJSON },
                    { DevicePlatform.Android, androidFileTypeJSON },
                });

            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Playlist JSON file",
                FileTypes = customFileType
            });

            if (result != null)
            {
                using var stream = await result.OpenReadAsync();
                using var reader = new StreamReader(stream);
                var jsonContent = await reader.ReadToEndAsync();

                var importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);

                if (importedPlaylist != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Playlists.Add(importedPlaylist);
                        SelectedPlaylist = importedPlaylist;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", $"Failed to import: {ex.Message}", "OK");
        }
    }

    private void LoadDummyData()
    {
        Playlist dummyList = new()
        {
            Name = "Test",
            CreatedAt = DateTime.Now
        };

        dummyList.Items.Add(new PlaylistItem { Title = "Skarrsinger Karmelita", Url = "https://www.youtube.com/watch?v=dhc4WFEeZfw&list=RDdhc4WFEeZfw&start_radio=1", StartTime = 5466, EndTime = 5573 });
        dummyList.Items.Add(new PlaylistItem { Title = "76", Url = "https://static.bandlab.com/revisions-formatted/7277ea00-254d-9b8c-0eab-c6b7bf94e414/7277ea00-254d-9b8c-0eab-c6b7bf94e414.m4a" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Empty", CreatedAt = DateTime.Now });

        SelectedPlaylist = dummyList;
    }
}