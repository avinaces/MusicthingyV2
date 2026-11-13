using YoutubeExplode;

namespace Musicthingy.Services;

// ────────────────────────────────────────────────────────────────────────────
// FastTrackDownloader — COMPATIBILITY FACADE (v5).
//
// The fast engine (parallel chunked downloads, YoutubeExplode n-deciphered
// URLs, persistent queue) now lives INSIDE TrackCacheService, so every
// download lands in the ONE cache directory that the Cache Statistics
// card, the Cached Tracks playlist and the MaxCacheSizeSetting limit all
// drive off (v4 kept a private "fasttracks" folder here — the stats page
// never saw those files, which is why caching looked broken).
//
// This class remains so existing call sites keep compiling; it simply
// forwards to the TrackCacheService singleton:
//   FastTrackDownloader.Enqueue(...)          → service.EnqueueFast(...)
//   FastTrackDownloader.GetCachedTrackPath(..)→ service.GetCachedTrackPath(..)
//   FastTrackDownloader.DownloadTrackAsync(..)→ service.CacheTrackFastAsync(..)
//   FastTrackDownloader.ClearAsync()          → service.ClearCacheAsync()
//
// NEW CODE should call TrackCacheService directly (inject it — it is
// already registered in DI) or use TrackCacheService.Instance.
// ────────────────────────────────────────────────────────────────────────────
public static class FastTrackDownloader
{
    private static TrackCacheService? Service => TrackCacheService.Instance;

    /// <summary>
    /// Queues a track for background caching. Fire-and-forget, deduplicated,
    /// never cancelled by track switches. See TrackCacheService.EnqueueFast.
    /// </summary>
    public static void Enqueue(YoutubeClient? yteClient, string trackUrl, string? fallbackDirectUrl)
        => Service?.EnqueueFast(yteClient, trackUrl, fallbackDirectUrl);

    /// <summary>Returns the cached file path for a track URL, or null.</summary>
    public static string? GetCachedTrackPath(string trackUrl)
        => Service?.GetCachedTrackPath(trackUrl);

    /// <summary>Downloads a track now. See TrackCacheService.CacheTrackFastAsync.</summary>
    public static Task<bool> DownloadTrackAsync(
        YoutubeClient? yteClient,
        string trackUrl,
        string? fallbackDirectUrl,
        CancellationToken ct)
        => Service?.CacheTrackFastAsync(yteClient, trackUrl, fallbackDirectUrl, ct)
           ?? Task.FromResult(false);

    /// <summary>Deletes every cached track. See TrackCacheService.ClearCacheAsync.</summary>
    public static Task ClearAsync()
    {
        Service?.ClearCacheAsync();
        return Task.CompletedTask;
    }
}
