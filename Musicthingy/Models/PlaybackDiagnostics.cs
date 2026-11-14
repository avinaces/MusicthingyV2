namespace Musicthingy.Models;

/// <summary>
/// A point-in-time snapshot of the current track's technical playback details,
/// rendered by the Playback settings diagnostics panel. Values are raw so the
/// view can decide how to format them.
/// </summary>
public sealed record PlaybackDiagnostics
{
    public static readonly PlaybackDiagnostics Empty = new();

    public bool HasTrack { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? PlaylistName { get; init; }
    public string? TrackUrl { get; init; }

    public bool IsPlaying { get; init; }
    public bool IsBuffering { get; init; }
    public bool IsCached { get; init; }

    public string? Source { get; init; }
    public string? StreamType { get; init; }
    public string? Client { get; init; }
    public string? Codec { get; init; }
    public string? Container { get; init; }
    public int? BitrateKbps { get; init; }
    public int? SampleRateHz { get; init; }
    public int? Itag { get; init; }

    public double PositionMs { get; init; }
    public double DurationMs { get; init; }
    public double BufferedMs { get; init; }
    public long? SabrBufferedBytes { get; init; }
    public long ResolveMs { get; init; }
    public string? StreamUrl { get; init; }

    /// <summary>Size of the local/cached file on disk, when playing one.</summary>
    public long? FileSizeBytes { get; init; }
}
