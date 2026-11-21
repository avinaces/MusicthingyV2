using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Musicthingy.Helpers;

namespace Musicthingy.Services;

/// <summary>
/// Resolves the BEST available artwork for a track beyond what was saved
/// in IconUrl at import time. YouTube imports save mqdefault.jpg
/// (320x180) - fine for a 44dp row thumbnail, hopeless for the full
/// player's ~360dp cover. maxresdefault.jpg (1280x720+) exists for
/// virtually every video, so we upgrade the URL and re-fetch.
///
/// Round 8: the video id is extracted from ANY YouTube-shaped URL the
/// track carries - including the SAVED THUMBNAIL URL itself
/// (img.youtube.com/vi/&lt;id&gt;/mqdefault.jpg), which is what YouTube
/// imports actually store in IconUrl. Round 7 only used
/// VideoId.TryParse, which rejects thumbnail URLs - the upgrade silently
/// never ran.
///
/// CACHE SEAM (intentionally NOT a cache yet): everything funnels through
/// TryGetArtworkAsync(). Adding a disk/memory cache later means filling in
/// the two marked spots in TryDownloadAsync - no call sites change.
/// </summary>
public static class MediaResolutionService
{
    /// <summary>
    /// Returns high-res artwork bytes for the track, or null when nothing
    /// better than the saved icon is available. Callers: use the returned
    /// bytes if non-null, otherwise fall back to the EXISTING saved icon
    /// (do not clear it).
    /// </summary>
    public static async Task<byte[]?> TryGetArtworkAsync(string? iconUrl, int minPixelWidth)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return null;

        string? videoId = ExtractVideoId(iconUrl);
        if (videoId == null)
        {
            // A plain video URL that YoutubeExplode rejects, a local file,
            // a data URI - no YouTube identity, nothing to upgrade.
            return null;
        }

        string[] candidates =
        {
            $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg", // 1280x720 (best)
            $"https://img.youtube.com/vi/{videoId}/hq720.jpg",        // 720x720-ish
            $"https://img.youtube.com/vi/{videoId}/sddefault.jpg",    // 640x480 letterboxed (last resort)
        };

        foreach (string candidate in candidates)
        {
            byte[]? bytes = await TryDownloadAsync(candidate, minPixelWidth);
            if (bytes != null)
                return bytes;
        }

        return null;
    }

    /// <summary>
    /// Round 8: video-id extraction that handles the URLs actually stored in
    /// IconUrl. YoutubeExplode's VideoId.TryParse accepts watch/share
    /// canonical forms but REJECTS thumbnail URLs
    /// (img.youtube.com/vi/&lt;id&gt;/mqdefault.jpg) - and those are exactly
    /// what YouTube imports save. Order: thumbnail /vi/ path, youtu.be,
    /// v= query, then the YoutubeExplode fallback. Internal:
    /// PlaylistItem.HiResArtwork reuses it for its cache-key probe (one
    /// implementation, two call sites).
    /// </summary>
    internal static string? ExtractVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        int vi = url.IndexOf("/vi/", StringComparison.Ordinal);
        if (vi >= 0)
        {
            int start = vi + 4;
            int end = url.IndexOf('/', start);
            if (end < 0) end = url.Length;
            string id = url[start..end];
            if (IsPlausibleVideoId(id))
                return id;
        }

        int be = url.IndexOf("youtu.be/", StringComparison.OrdinalIgnoreCase);
        if (be >= 0)
        {
            int start = be + 9;
            int end = url.IndexOf('?', start);
            if (end < 0) end = url.Length;
            string id = url[start..end].TrimEnd('/');
            if (IsPlausibleVideoId(id))
                return id;
        }

        int v = url.IndexOf("v=", StringComparison.Ordinal);
        if (v >= 0)
        {
            int start = v + 2;
            int end = url.IndexOf('&', start);
            if (end < 0) end = url.Length;
            string id = url[start..end];
            if (IsPlausibleVideoId(id))
                return id;
        }

        return MediaUrlHelper.GetYouTubeVideoId(url);
    }

    private static bool IsPlausibleVideoId(string id)
        => id.Length == 11 && id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Android 14) Musicthingy");
        return client;
    }

    /// <summary>
    /// Downloads a thumbnail candidate and rejects it if it is below the
    /// requested pixel width (img.youtube.com serves a 120x90 gray
    /// placeholder instead of a 404 for missing sizes, so a byte-level
    /// check is required).
    ///
    /// CACHE SEAM: disk/memory lookup would go HERE (before the network
    /// request), keyed on the candidate URL.
    /// </summary>
    private static async Task<byte[]?> TryDownloadAsync(string url, int minPixelWidth)
    {
        try
        {
            using var response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes == null || bytes.Length == 0)
                return null;

            // Decode bounds only (no full bitmap allocation).
            var (w, h) = ImageHeaderInspector.GetJpegSize(bytes);
            if (w < minPixelWidth || h < 1)
                return null;

            // CACHE SEAM: store would go HERE (after a successful fetch).

            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
