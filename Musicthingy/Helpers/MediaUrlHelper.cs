using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace Musicthingy.Helpers;

public static class MediaUrlHelper
{
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
}