namespace Musicthingy.Models;

/// <summary>
/// Contains the resolved direct stream URL and the matching User-Agent required to access it.
/// The remaining fields are optional playback diagnostics (source, client, codec, ...)
/// surfaced in the Playback settings panel; playback itself only needs Url + UserAgent.
/// </summary>
public record ResolvedMedia(
    string Url,
    string UserAgent,
    string? Source = null,
    string? StreamType = null,
    string? Client = null,
    string? Codec = null,
    string? Container = null,
    int? BitrateKbps = null,
    int? SampleRateHz = null,
    int? Itag = null);
