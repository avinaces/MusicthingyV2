using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
using Musicthingy.Settings.Definitions;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.Services;

// ────────────────────────────────────────────────────────────────────────────
// TrackCacheService — the ONE track cache (stats page + Cached Tracks
// playlist + MaxCacheSizeSetting all drive off this service's events).
//
// v5 merges the FastTrackDownloader engine INTO this service. Until now the
// fast engine wrote to its own directory (Cache/fasttracks) with its own
// index and NO events: the Storage "Cache Statistics" card never moved,
// the download progress card never appeared and the "Cached Tracks"
// playlist never listed fast-cached tracks — it looked like caching was
// broken even though the files landed on disk. One cache directory
// (Cache/TrackCache), one naming scheme (SHA256 of the track URL, no
// extension — same as it always was), one LRU limit (MaxCacheSizeSetting),
// one event stream:
//
//   • CacheUpdated              → stats card + Cached Tracks playlist refresh
//   • DownloadProgressChanged   → live progress card in the stats page
//
// FAST PATH (why it exists): playback stream URLs carry an UNTRANSFORMED
// "n" parameter; Google Video throttles those to ~40-80 kB/s. YoutubeExplode
// deciphers n, so URLs taken from its manifest download at full speed. The
// download itself runs as 4 MiB chunks over 3 parallel HTTP range requests
// with per-chunk retries — InnerTune-style parallelism. The throttled
// playback URL remains as a fallback (the throttle is per connection, so
// even the fallback gets ~3x through the parallel engine).
//
// QUEUE: every played track is ENQUEUED and drained sequentially in the
// background (one at a time, single-flight). Downloads are NOT cancelled
// by track switches — skimmed tracks still end up cached. This mirrors how
// InnerTune's DownloadManager keeps a persistent download queue.
//
// MIGRATION: files previously downloaded by the old FastTrackDownloader
// (Cache/fasttracks + index.json) are moved into this cache at startup, so
// nothing the user already downloaded is lost.
// ────────────────────────────────────────────────────────────────────────────
public class TrackCacheService
{
    private readonly string _cacheDir;
    private readonly HttpClient _httpClient;
    private readonly SettingsManager _settingsManager;

    /// <summary>Singleton handle for static callers (FastTrackDownloader facade).</summary>
    public static TrackCacheService? Instance { get; private set; }

    public event EventHandler? CacheUpdated;

    /// <summary>Fires when a download's progress changes (URL, downloaded bytes, total bytes, speed KB/s).</summary>
    public event Action<DownloadProgress>? DownloadProgressChanged;

    public record DownloadProgress(string Url, long DownloadedBytes, long? TotalBytes, double SpeedKBps);

    // ── Fast engine configuration ──────────────────────────────────────────
    private const int ChunkSizeBytes = 4 * 1024 * 1024;   // 4 MiB per chunk
    private const int ParallelConnections = 3;             // parallel range requests per track
    private const int MaxChunkRetries = 3;
    private const int ManifestTimeoutSeconds = 90;
    private const int MaxQueueDepth = 64;

    public bool IsTrackCached(string originalUrl) => !string.IsNullOrEmpty(originalUrl) && GetCachedTrackPath(originalUrl) != null;

    public TrackCacheService(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _cacheDir = Path.Combine(FileSystem.CacheDirectory, "TrackCache");
        // Use the shared IPv4-forced handler — same as the resolver, avoids
        // IPv6 DNS issues and shares the connection pool with playback.
        _httpClient = new HttpClient(YouTubeResolverService.Ipv4Handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);

        Instance = this;

        CleanupTempFiles();
        MigrateLegacyFastCache();
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[Cache] {message}");
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }

    private string GetSafeId(string url)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash);
    }

    public string? GetCachedTrackPath(string originalUrl)
    {
        string id = GetSafeId(originalUrl);
        string path = Path.Combine(_cacheDir, id);

        if (File.Exists(path))
        {
            // Update last access time for LRU (Least Recently Used) cleanup
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API — legacy surface (same behavior as before the merge)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Legacy entry point. Queues a background download of the given direct
    /// stream URL through the fast engine. Kept for source compatibility —
    /// new callers should use <see cref="EnqueueFast"/> and pass the
    /// YoutubeExplode client so the manifest can be resolved at full speed.
    /// </summary>
    public Task CacheTrackAsync(string originalUrl, string streamUrl)
    {
        EnqueueFast(null, originalUrl, streamUrl);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Fast engine + persistent queue
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record CacheRequest(YoutubeClient? Client, string TrackUrl, string? FallbackUrl);

    private readonly ConcurrentQueue<CacheRequest> _cacheQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queuedUrls = new();
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private Task? _drainLoop;
    private readonly object _drainLock = new();

    /// <summary>
    /// Queues a track for background caching. Fire-and-forget: deduplicated,
    /// skips tracks already on disk and NEVER cancelled by track switches —
    /// the drain loop keeps working while the user skips around the
    /// playlist. The ViewModel calls this for the current track AND the
    /// next track in the queue (pre-cache, InnerTune-style).
    /// </summary>
    public void EnqueueFast(YoutubeClient? yteClient, string trackUrl, string? fallbackDirectUrl)
    {
        if (string.IsNullOrEmpty(trackUrl)) return;
        try
        {
            if (GetCachedTrackPath(trackUrl) != null) return;   // already on disk
            if (!_queuedUrls.TryAdd(trackUrl, 0)) return;        // already queued

            if (_cacheQueue.Count >= MaxQueueDepth)
            {
                _queuedUrls.TryRemove(trackUrl, out _);
                Log($"cache queue full ({MaxQueueDepth}) — not caching {trackUrl}");
                return;
            }

            _cacheQueue.Enqueue(new CacheRequest(yteClient, trackUrl, fallbackDirectUrl));
            _queueSignal.Release();
            Log($"queued for background caching ({_cacheQueue.Count} pending): {trackUrl}");
            EnsureDrainLoop();
        }
        catch (Exception ex)
        {
            Log($"enqueue failed for {trackUrl}: {ex.Message}");
        }
    }

    private void EnsureDrainLoop()
    {
        lock (_drainLock)
        {
            if (_drainLoop == null || _drainLoop.IsCompleted)
                _drainLoop = Task.Run(DrainCacheQueueAsync);
        }
    }

    private async Task DrainCacheQueueAsync()
    {
        // One worker, forever. Track switches do NOT touch this loop.
        while (true)
        {
            await _queueSignal.WaitAsync();

            CacheRequest? req = null;
            try { _cacheQueue.TryDequeue(out req); } catch { }
            if (req == null) continue;

            _queuedUrls.TryRemove(req.TrackUrl, out _);

            try
            {
                // CancellationToken.None ON PURPOSE: a queued download must
                // survive track switches. Per-request deadlines still apply
                // inside (manifest 90 s, throttled fallback 5 min).
                await CacheTrackFastAsync(req.Client, req.TrackUrl, req.FallbackUrl, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // detached per-download deadlines only — nothing to cancel here
            }
            catch (Exception ex)
            {
                Log($"background cache failed for {req.TrackUrl}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Downloads a track into the cache at full speed. Returns true when the
    /// track is cached afterwards (freshly downloaded or already present).
    /// Raises <see cref="DownloadProgressChanged"/> while downloading and
    /// <see cref="CacheUpdated"/> when the file lands (the cache statistics
    /// card and the Cached Tracks playlist update live, like they always did).
    /// </summary>
    /// <param name="yteClient">
    /// YoutubeExplode client — the cookie client when available (YoutubeExplode
    /// deciphers the n parameter, so its URLs download at full speed). Pass
    /// null for plain direct URLs (non-YouTube tracks).
    /// </param>
    /// <param name="trackUrl">Original track URL (the cache key).</param>
    /// <param name="fallbackDirectUrl">
    /// The playback stream URL from the InnerTube resolver — used only when
    /// YoutubeExplode failed. It is n-throttled (~50-80 kB/s) but still works.
    /// </param>
    public async Task<bool> CacheTrackFastAsync(
        YoutubeClient? yteClient,
        string trackUrl,
        string? fallbackDirectUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trackUrl)) return false;
        if (GetCachedTrackPath(trackUrl) != null) return true;

        // Declared at METHOD scope on purpose (not a 'using var', not inside
        // the try): the fallback path assigns it conditionally (C# forbids
        // reassigning a using variable — CS1654) and the finally below must
        // dispose it (a local declared inside the try block would be out of
        // scope there — CS0103).
        CancellationTokenSource? fallbackCts = null;

        await _singleFlight.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: a finished download may have raced us.
            if (GetCachedTrackPath(trackUrl) != null) return true;

            string streamUrl = "";
            long totalBytes = -1;

            // ── 1. Preferred: YoutubeExplode (deciphered n → full speed) ────
            if (yteClient != null)
            {
                try
                {
                    using var mcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    mcts.CancelAfter(TimeSpan.FromSeconds(ManifestTimeoutSeconds));

                    VideoId videoId = VideoId.Parse(trackUrl);
                    StreamManifest manifest = await yteClient.Videos.Streams.GetManifestAsync(videoId, mcts.Token);

                    AudioOnlyStreamInfo? best = manifest.GetAudioOnlyStreams()
                        .OrderByDescending(s => s.AudioCodec.Contains("opus", StringComparison.OrdinalIgnoreCase) ? 3 :
                                                 s.AudioCodec.Contains("mp4a", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                        .ThenByDescending(s => s.Bitrate)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(best?.Url))
                    {
                        streamUrl = best!.Url;
                        totalBytes = best.Size.Bytes;
                        Log($"manifest OK ({best.AudioCodec}, {totalBytes} bytes) — full-speed download");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log("manifest timed out — will try playback-URL fallback");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"manifest failed: {ex.Message}");
                }
            }

            // ── 2. Fallback: the playback URL (throttled but functional) ────
            // The n-throttle (~40-80 kB/s) is PER CONNECTION, so this
            // fallback also goes through the parallel-range engine once
            // the size is known (3 connections ≈ 3x throughput). It runs
            // on a DETACHED token with a hard 5-minute deadline so track
            // switches cannot kill it mid-download.
            bool usingFallback = false;
            CancellationToken downloadCt = ct;

            if (string.IsNullOrEmpty(streamUrl) && !string.IsNullOrEmpty(fallbackDirectUrl))
            {
                streamUrl = fallbackDirectUrl;
                usingFallback = true;
                fallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                downloadCt = fallbackCts.Token;
                Log("using direct-URL fallback (throttled for YouTube; skip-proof with 5-min deadline)");
            }

            if (string.IsNullOrEmpty(streamUrl)) return false;

            // ── 3. Download into the SAME cache dir with the SAME naming ────
            // (SHA256 of the track URL, no extension) so GetCachedTrackPath,
            // IsTrackCached, the statistics card and the Cached Tracks
            // playlist all see the file immediately.
            string finalPath = Path.Combine(_cacheDir, GetSafeId(trackUrl));
            string tempPath = finalPath + "." + Guid.NewGuid() + ".tmp";

            try
            {
                // Fallback with unknown size: probe once (Range: bytes=0-0
                // → Content-Range total) so the parallel chunk engine can
                // be used.
                if (usingFallback && totalBytes <= 0)
                {
                    (long size, string contentType) = await ProbeAsync(streamUrl, downloadCt);
                    if (size > 0)
                    {
                        totalBytes = size;
                        Log($"fallback size probe: {size} bytes ({contentType})");
                    }
                }

                if (totalBytes > 0)
                {
                    await DownloadParallelChunksAsync(trackUrl, streamUrl, tempPath, totalBytes, downloadCt);
                }
                else
                {
                    await DownloadSequentialAsync(trackUrl, streamUrl, tempPath, downloadCt);
                }

                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tempPath, finalPath);

                // Final progress report (100%) — the stats page's progress
                // card shows "Done — X MB" and then hides itself.
                long finalSize = new FileInfo(finalPath).Length;
                DownloadProgressChanged?.Invoke(new DownloadProgress(trackUrl, finalSize, finalSize, 0));

                EnforceCacheLimit();
                CacheUpdated?.Invoke(this, EventArgs.Empty);
                Log($"cached {finalSize} bytes: {trackUrl}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"download failed for {trackUrl}: {ex.Message}");
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            fallbackCts?.Dispose();
            _singleFlight.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Download engines
    // ─────────────────────────────────────────────────────────────────────────

    private async Task DownloadParallelChunksAsync(
        string trackUrl, string url, string tempPath, long totalBytes, CancellationToken ct)
    {
        // Preallocate the file so parallel writers can use positional writes.
        using SafeFileHandle handle = File.OpenHandle(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        RandomAccess.SetLength(handle, totalBytes);

        // Shared progress state across the parallel chunks.
        long downloadedBytes = 0;
        long lastReportBytes = 0;
        long lastReportTicks = Stopwatch.GetTimestamp();
        var progressLock = new object();

        void ReportProgress(bool force = false)
        {
            lock (progressLock)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedSec = (now - lastReportTicks) / (double)Stopwatch.Frequency;
                bool timeDue = elapsedSec > 0.5;
                bool bytesDue = downloadedBytes - lastReportBytes > 524288;
                if (!force && !timeDue && !bytesDue) return;

                double speedKBps = elapsedSec > 0
                    ? (downloadedBytes - lastReportBytes) / 1024.0 / elapsedSec
                    : 0;
                lastReportBytes = downloadedBytes;
                lastReportTicks = now;

                DownloadProgressChanged?.Invoke(new DownloadProgress(
                    trackUrl, downloadedBytes, totalBytes, speedKBps));
            }
        }

        var chunks = new List<(long Start, long End)>();
        for (long start = 0; start < totalBytes; start += ChunkSizeBytes)
        {
            long end = Math.Min(start + ChunkSizeBytes - 1, totalBytes - 1);
            chunks.Add((start, end));
        }

        using var gate = new SemaphoreSlim(ParallelConnections, ParallelConnections);
        var tasks = chunks.Select(chunk => Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await DownloadChunkWithRetryAsync(
                    url, chunk.Start, chunk.End, totalBytes, handle, ct,
                    onBytes: n =>
                    {
                        Interlocked.Add(ref downloadedBytes, n);
                        ReportProgress();
                    });
            }
            finally
            {
                gate.Release();
            }
        }, ct));

        await Task.WhenAll(tasks);
        ReportProgress(force: true);
    }

    private async Task<long> DownloadChunkWithRetryAsync(
        string url, long start, long end, long totalBytes, SafeFileHandle handle, CancellationToken ct,
        Action<long>? onBytes = null)
    {
        long expected = end - start + 1;

        for (int attempt = 1; attempt <= MaxChunkRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                // A 200 (instead of 206) means the server ignored the range.
                // That is only acceptable when the request covers the whole file.
                bool isFullFile = start == 0 && expected == totalBytes;
                if ((int)response.StatusCode != 206 && !isFullFile)
                    throw new IOException($"range request returned {(int)response.StatusCode} for {start}-{end}");

                await using Stream src = await response.Content.ReadAsStreamAsync(ct);

                byte[] buffer = new byte[1024 * 1024];
                long offset = start;
                long received = 0;

                while (true)
                {
                    int read = await src.ReadAsync(buffer, ct);
                    if (read == 0) break;

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct);
                    offset += read;
                    received += read;
                    onBytes?.Invoke(read);

                    // A 200 body would deliver bytes from position 0 — reject.
                    if (!isFullFile && received > expected)
                        throw new IOException("server ignored Range header");
                }

                if (received != expected)
                    throw new IOException($"short chunk read {received}/{expected} ({start}-{end})");

                return received; // chunk complete
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < MaxChunkRetries)
            {
                int backoffMs = 500 * attempt * attempt; // 0.5s → 2s → 4.5s
                Log($"chunk {start}-{end} attempt {attempt} failed: {ex.Message} — retry in {backoffMs}ms");
                await Task.Delay(backoffMs, ct);
            }
        }

        throw new IOException($"chunk {start}-{end} failed after {MaxChunkRetries} attempts");
    }

    /// <summary>
    /// Probes a stream URL for its total size.
    /// Uses "Range: bytes=0-0" so a 206 answer carries Content-Range
    /// (bytes 0-0/TOTAL); a server that ignores ranges answers 200 and
    /// Content-Length instead. Never throws.
    /// </summary>
    private async Task<(long Total, string ContentType)> ProbeAsync(
        string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                Log($"fallback probe returned {(int)response.StatusCode} (URL expired? resolver cache holds URLs 2 h)");
                return (0, "");
            }

            long total = 0;
            string? contentRange = response.Content.Headers.ContentRange?.ToString();
            if (contentRange != null)
            {
                int slash = contentRange.LastIndexOf('/');
                if (slash >= 0 && long.TryParse(contentRange[(slash + 1)..], out long t))
                    total = t;
            }
            if (total <= 0 && response.Content.Headers.ContentLength is long len)
                total = len; // range-ignoring server: full body length

            return (total, response.Content.Headers.ContentType?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            Log($"fallback probe failed: {ex.Message}");
            return (0, "");
        }
    }

    private async Task DownloadSequentialAsync(
        string trackUrl, string url, string tempPath, CancellationToken ct)
    {
        // Unknown size (non-YouTube or range-less server): plain streaming
        // copy, still reporting progress like the legacy path did.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        long downloadedBytes = 0;
        long lastReportBytes = 0;
        long lastReportTicks = Stopwatch.GetTimestamp();

        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        byte[] buffer = new byte[65536];
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloadedBytes += read;

            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (now - lastReportTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec > 0.5)
            {
                double speedKBps = (downloadedBytes - lastReportBytes) / 1024.0 / elapsedSec;
                lastReportBytes = downloadedBytes;
                lastReportTicks = now;
                DownloadProgressChanged?.Invoke(new DownloadProgress(
                    trackUrl, downloadedBytes, totalBytes, speedKBps));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Housekeeping (unchanged legacy surface — now also covers fast files,
    //  because everything lives in the same directory)
    // ─────────────────────────────────────────────────────────────────────────

    public void EnforceCacheLimit()
    {
        try
        {
            var maxSetting = _settingsManager.GetVisibleSettings().OfType<MaxCacheSizeSetting>().FirstOrDefault();
            int maxMB = maxSetting?.Value ?? 1024;

            // 0 = unlimited — don't enforce any limit
            if (maxMB == 0)
            {
                Debug.WriteLine("[Cache] Cache limit is 0 (unlimited) — skipping enforcement");
                return;
            }

            long maxBytes = maxMB * 1048576L;

            var files = new DirectoryInfo(_cacheDir).GetFiles()
                .Where(f => !f.Name.EndsWith(".tmp"))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            long currentSize = files.Sum(f => f.Length);

            if (currentSize <= maxBytes)
            {
                Debug.WriteLine($"[Cache] Within limit: {currentSize / 1048576.0:F1} MB / {maxMB} MB");
                return;
            }

            Debug.WriteLine($"[Cache] Over limit: {currentSize / 1048576.0:F1} MB / {maxMB} MB — evicting oldest tracks");

            foreach (var file in files)
            {
                if (currentSize <= maxBytes) break;
                currentSize -= file.Length;
                file.Delete();
                Debug.WriteLine($"[Cache] Evicted: {file.Name}");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Cache] Cleanup failed: {ex.Message}"); }
        CacheUpdated?.Invoke(this, EventArgs.Empty);
    }

    public long GetTotalCacheSizeBytes()
    {
        if (!Directory.Exists(_cacheDir)) return 0;
        return new DirectoryInfo(_cacheDir).EnumerateFiles().Where(f => !f.Name.EndsWith(".tmp")).Sum(f => f.Length);
    }

    public Task ClearCacheAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                foreach (var file in new DirectoryInfo(_cacheDir).GetFiles()) file.Delete();
                CacheUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        });
    }

    private void CleanupTempFiles()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_cacheDir).GetFiles("*.tmp")) file.Delete();
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log($"delete failed for {path}: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One-time migration of the old FastTrackDownloader cache (v4 and older)
    // ─────────────────────────────────────────────────────────────────────────

    private void MigrateLegacyFastCache()
    {
        try
        {
            string legacyDir = Path.Combine(FileSystem.CacheDirectory, "fasttracks");
            if (!Directory.Exists(legacyDir)) return;

            int moved = 0;
            string indexPath = Path.Combine(legacyDir, "index.json");
            if (File.Exists(indexPath))
            {
                // index.json shape: { "<trackUrl>": { "File": "<name>", ... } }
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("File", out var fileProp) ||
                        fileProp.ValueKind != JsonValueKind.String) continue;

                    string src = Path.Combine(legacyDir, fileProp.GetString()!);
                    string dst = Path.Combine(_cacheDir, GetSafeId(prop.Name));
                    if (File.Exists(src) && !File.Exists(dst))
                    {
                        File.Move(src, dst);
                        moved++;
                    }
                }
            }

            // Remove the legacy directory with everything left in it.
            Directory.Delete(legacyDir, recursive: true);
            Log($"migrated {moved} legacy fast-cache file(s) into the track cache");
            if (moved > 0)
                CacheUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log($"legacy fast-cache migration failed: {ex.Message}");
        }
    }
}
