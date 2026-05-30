namespace Musicthingy.Models;

/// <summary>
/// Contains the resolved direct stream URL and the matching User-Agent required to access it.
/// </summary>
public record ResolvedMedia(string Url, string UserAgent);