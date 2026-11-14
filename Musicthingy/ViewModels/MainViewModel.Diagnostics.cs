using Musicthingy.Models;
using Musicthingy.Services;
using Musicthingy.Services.Sabr;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const int DiagnosticsUpdateIntervalMs = 500;

    private ResolvedMedia? _currentResolvedMedia;
    private string? _currentResolvedMediaTrackUrl;
    private long _currentResolveMs;
    private bool _currentIsCached;
    private long _lastDiagnosticsTicks;

    // Header sniffing result for the local/cached file now playing (null for
    // network streams). Cache files carry no extension, so this is the only
    // way the panel can report their real format.
    private LocalMediaInspector.LocalMediaInfo? _currentLocalMediaInfo;

    /// <summary>
    /// Raised when the current track's technical playback details change.
    /// Always raised on the main thread.
    /// </summary>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>
    /// Builds the snapshot shown by the Playback settings diagnostics panel.
    /// Safe to call from the main thread; player getters are mirrored.
    /// </summary>
    public PlaybackDiagnostics GetPlaybackDiagnostics()
    {
        PlaylistItem? track = CurrentTrack;
        if (track == null)
            return PlaybackDiagnostics.Empty;

        double startMs = (track.StartTime ?? 0) * 1000.0;

        double durationMs = Duration.TotalMilliseconds;
        if (durationMs <= 0)
            durationMs = _playbackService.LengthMs;
        if (durationMs <= 0 && track.Duration.TotalSeconds > 0)
            durationMs = track.Duration.TotalMilliseconds;

        double positionMs = _playbackService.CurrentTimeMs - startMs;
        if (positionMs < 0) positionMs = 0;

        double bufferedMs = _playbackService.BufferedPositionMs - startMs;
        if (bufferedMs < positionMs) bufferedMs = positionMs;

        // Only trust the stored resolver result when it belongs to this track:
        // during a track switch the previous stream's details stay around for
        // a moment until the new resolve completes.
        ResolvedMedia? media = _currentResolvedMedia != null &&
                               string.Equals(_currentResolvedMediaTrackUrl, track.Url, StringComparison.Ordinal)
            ? _currentResolvedMedia
            : null;

        long? sabrBytes = null;
        if (media?.Url.StartsWith("sabr://", StringComparison.OrdinalIgnoreCase) == true)
            sabrBytes = SabrStreamRegistry.Find(media.Url)?.Buffer.TotalLength;

        // A local/cached file has no resolver metadata: fill the gaps from the
        // file itself (header sniffing + size/duration bitrate).
        string? codec = media?.Codec;
        string? container = media?.Container;
        int? sampleRate = media?.SampleRateHz;
        int? bitrate = media?.BitrateKbps;
        long? fileBytes = null;

        if (_currentLocalMediaInfo is { } local)
        {
            codec ??= local.Codec;
            container ??= local.Container;
            sampleRate ??= local.SampleRateHz;
            fileBytes = local.FileSizeBytes;

            if (bitrate == null && durationMs > 0)
            {
                double kbps = local.FileSizeBytes * 8.0 / (durationMs / 1000.0) / 1000.0;
                if (kbps > 0 && kbps < 10000) bitrate = (int)Math.Round(kbps);
            }
        }

        return new PlaybackDiagnostics
        {
            HasTrack = true,
            Title = track.Title,
            PlaylistName = track.Playlist?.Name,
            TrackUrl = track.Url,
            IsPlaying = IsPlaying,
            IsBuffering = IsLoadingMedia,
            IsCached = _currentIsCached && media != null,
            Source = media?.Source,
            StreamType = media?.StreamType,
            Client = media?.Client,
            Codec = codec,
            Container = container,
            BitrateKbps = bitrate,
            SampleRateHz = sampleRate,
            Itag = media?.Itag,
            PositionMs = positionMs,
            DurationMs = durationMs,
            BufferedMs = bufferedMs,
            SabrBufferedBytes = sabrBytes,
            ResolveMs = media != null ? _currentResolveMs : 0,
            StreamUrl = media?.Url,
            FileSizeBytes = fileBytes,
        };
    }

    /// <summary>Records what the resolver produced for the track now playing.</summary>
    private void SetResolvedMedia(ResolvedMedia? media, bool isCached, long resolveMs, string? trackUrl)
    {
        _currentResolvedMedia = media;
        _currentResolvedMediaTrackUrl = media != null ? trackUrl : null;
        _currentIsCached = isCached;
        _currentResolveMs = resolveMs;
        _currentLocalMediaInfo = media != null ? LocalMediaInspector.Inspect(ToLocalPath(media.Url)) : null;
        NotifyDiagnosticsChanged();
    }

    /// <summary>The file path behind a local media URL, or null for streams.</summary>
    private static string? ToLocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.LocalPath : null;

        return url.StartsWith("/", StringComparison.Ordinal) ? url : null;
    }

    /// <summary>Raises <see cref="DiagnosticsChanged"/> on the main thread.</summary>
    public void NotifyDiagnosticsChanged()
    {
        _lastDiagnosticsTicks = Environment.TickCount64;
        RaiseDiagnosticsChanged();
    }

    /// <summary>Throttled variant used by the high-frequency position tick.</summary>
    private void NotifyDiagnosticsThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastDiagnosticsTicks < DiagnosticsUpdateIntervalMs) return;
        _lastDiagnosticsTicks = now;
        RaiseDiagnosticsChanged();
    }

    private void RaiseDiagnosticsChanged()
    {
        if (MainThread.IsMainThread)
            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        else
            MainThread.BeginInvokeOnMainThread(() => DiagnosticsChanged?.Invoke(this, EventArgs.Empty));
    }
}
