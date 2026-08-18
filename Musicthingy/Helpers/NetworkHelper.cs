using Musicthingy.Helpers;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace Musicthingy.Helpers;

public static class NetworkHelper
{
    private static readonly YoutubeClient _youtubeClient = new();

    public static async Task<(bool IsSuccess, string ErrorMessage, string? FetchedTitle, string? FetchedImageUrl)> CheckMediaUrlAsync(string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "URL cannot be empty.", null, null);
        }

        // ==========================================
        // 1. YOUTUBE URL HANDLING (Videos & Playlists)
        // ==========================================
        if (MediaUrlHelper.IsYouTubeUrl(url))
        {
            // A. Check if it's a YouTube Playlist
            if (MediaUrlHelper.IsYouTubePlaylistUrl(url))
            {
                try
                {
                    PlaylistId? playlistId = PlaylistId.TryParse(url);
                    if (playlistId != null)
                    {
                        Playlist playlist = await _youtubeClient.Playlists.GetAsync(playlistId.Value, token);

                        // Extract video ID from playlist thumbnail to force unpadded mqdefault.jpg
                        string? thumb = null;
                        if (playlist.Thumbnails.Count > 0)
                        {
                            string rawThumb = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? "";
                            string? thumbVideoId = MediaUrlHelper.GetYouTubeVideoId(rawThumb);
                            thumb = thumbVideoId != null
                                ? $"https://img.youtube.com/vi/{thumbVideoId}/mqdefault.jpg"
                                : rawThumb;
                        }

                        return (true, string.Empty, playlist.Title, thumb);
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Playlist unavailable or private: {ex.Message}", null, null);
                }
            }

            // B. Check if it's a YouTube Video
            string? videoId = MediaUrlHelper.GetYouTubeVideoId(url);
            if (videoId != null)
            {
                try
                {
                    Video video = await _youtubeClient.Videos.GetAsync(videoId, token);

                    // Strictly use MediaUrlHelper to get the unpadded mqdefault.jpg
                    string? thumb = MediaUrlHelper.GetYouTubeThumbnail(url);

                    return (true, string.Empty, video.Title, thumb);
                }
                catch (Exception ex)
                {
                    return (false, $"Video unavailable: {ex.Message}", null, null);
                }
            }

            return (false, "Invalid YouTube URL format.", null, null);
        }

        // ==========================================
        // 2. DIRECT HTTP / LOCAL FILE HANDLING
        // ==========================================
        if (MediaUrlHelper.IsLocalFile(url))
        {
            if (File.Exists(url))
            {
                string title = Path.GetFileNameWithoutExtension(url);
                return (true, string.Empty, title, null);
            }
            return (false, "Local file not found.", null, null);
        }

        if (MediaUrlHelper.IsWebUrl(url))
        {
            try
            {
                using HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.IsSuccessStatusCode)
                {
                    return (true, string.Empty, null, null);
                }

                return (false, $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}", null, null);
            }
            catch (Exception ex)
            {
                return (false, $"Could not reach URL: {ex.Message}", null, null);
            }
        }

        return (false, "Invalid URL or path format.", null, null);
    }
}