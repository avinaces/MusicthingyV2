using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using Musicthingy.Models;
using System.Collections.Concurrent;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private readonly YoutubeClient _youtubeClient = new();
    private static readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _resolvedCache = new();

    private PlaylistItem? _prefetchedTrack;
    private MediaSource? _prefetchedMediaSource;

    private async Task WarmPlaylistCacheAsync()
    {
#if ANDROID
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        await Parallel.ForEachAsync(PlaybackQueue, options, async (item, token) =>
        {
            await ResolveMediaSourceAsync(item);
        });
#else
        await Task.CompletedTask;
#endif
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

    private async Task<MediaSource?> ResolveMediaSourceAsync(PlaylistItem track, IProgress<double>? progress = null)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;
        string url = track.Url;

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                return await Task.Run(async () =>
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

                    if (File.Exists(tempFilePath))
                    {
                        progress?.Report(1.0);
                        return MediaSource.FromFile(tempFilePath);
                    }

                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                    var progressiveStream = manifest.GetMuxedStreams()
                        .Where(s => s.Container == YoutubeExplode.Videos.Streams.Container.Mp4)
                        .OrderByDescending(s => s.VideoQuality)
                        .FirstOrDefault();

                    string? targetUrl = progressiveStream?.Url ?? manifest.GetAudioOnlyStreams().GetWithHighestBitrate()?.Url;

                    if (!string.IsNullOrEmpty(targetUrl))
                    {
                        string ffmpegPath = await GetFFmpegExecutableAsync();
                        string downloadingFilePath = Path.Combine(cacheDir, $"{videoId.Value}_{Guid.NewGuid()}.temp.m4a");

                        double startPos = track.StartTime ?? 0;
                        string args = $"-y -user_agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\" -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -ss {startPos} -i \"{targetUrl}\" ";
                        if (track.EndTime.HasValue) args += $"-t {track.EndTime.Value - startPos} ";
                        args += $"-vn -c:a copy -f ipod \"{downloadingFilePath}\"";

                        var processInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ffmpegPath,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        progress?.Report(0.5);

                        using (var process = System.Diagnostics.Process.Start(processInfo))
                        {
                            if (process != null) await process.WaitForExitAsync();
                        }

                        if (File.Exists(downloadingFilePath))
                        {
                            var fileInfo = new FileInfo(downloadingFilePath);
                            if (fileInfo.Length > 0)
                            {
                                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                                File.Move(downloadingFilePath, tempFilePath);
                            }
                            else File.Delete(downloadingFilePath);
                        }

                        if (File.Exists(tempFilePath))
                        {
                            progress?.Report(1.0);
                            return MediaSource.FromFile(tempFilePath);
                        }

                        return MediaSource.FromUri(targetUrl);
                    }
#endif
                    return null;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                return null;
            }
        }

        return MediaSource.FromUri(url);
    }

#if WINDOWS
    private async Task<string> GetFFmpegExecutableAsync()
    {
        string cacheFfmpegPath = Path.Combine(FileSystem.CacheDirectory, "ffmpeg.exe");
        if (!File.Exists(cacheFfmpegPath))
        {
            string installDirFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(installDirFfmpeg)) await Task.Run(() => File.Copy(installDirFfmpeg, cacheFfmpegPath, true));
            else throw new FileNotFoundException($"ffmpeg.exe was not found: {installDirFfmpeg}");
        }
        return cacheFfmpegPath;
    }
#endif

    private void CleanupCache()
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            var files = Directory.GetFiles(cacheDir, "*.mp4");
            foreach (var file in files)
            {
                if (File.GetCreationTime(file) < DateTime.Now.AddDays(-3))
                    File.Delete(file);
            }
        }
        catch { }
    }
}