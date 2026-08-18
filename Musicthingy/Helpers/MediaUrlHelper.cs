using System.Security.Cryptography;
using System.Text;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace Musicthingy.Helpers;

public static class MediaUrlHelper
{
    // ==========================================
    // LOCAL & WEB DETECTION
    // ==========================================

    public static bool IsLocalFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.StartsWith("/", StringComparison.Ordinal) ||
               url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================
    // YOUTUBE SPECIFIC LOGIC
    // ==========================================

    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetYouTubeVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        VideoId? videoId = VideoId.TryParse(url);
        return videoId?.Value;
    }

    public static string? GetYouTubePlaylistId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        PlaylistId? playlistId = PlaylistId.TryParse(url);
        return playlistId?.Value;
    }

    public static bool IsYouTubeVideoUrl(string? url) => GetYouTubeVideoId(url) != null;

    public static bool IsYouTubePlaylistUrl(string? url) => GetYouTubePlaylistId(url) != null;

    public static bool HasBothVideoAndPlaylist(string? url)
    {
        return GetYouTubeVideoId(url) != null && GetYouTubePlaylistId(url) != null;
    }

    public static string? GetYouTubeThumbnail(string? url)
    {
        string? videoId = GetYouTubeVideoId(url);
        // Use mqdefault.jpg which is natively 16:9 (no black bars) instead of hqdefault.jpg
        return videoId != null ? $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg" : null;
    }

    // ==========================================
    // UTILITY LOGIC
    // ==========================================

    /// <summary>
    /// Extracts a clean host name to display in the UI (e.g. "youtube.com", or "Local File")
    /// </summary>
    public static string GetDisplayHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (IsLocalFile(url)) return "Local File";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;

        return string.Empty;
    }

    /// <summary>
    /// Generates a bulletproof, file-system-safe hash ID for the cache service.
    /// </summary>
    public static string GetSafeHashId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash);
    }
}