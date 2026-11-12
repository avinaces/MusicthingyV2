using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.Services;

// ────────────────────────────────────────────────────────────────────────────
// FastTrackDownloader — high-speed track cache engine.
//
// WHY IT EXISTS
// Playback stream URLs taken from the InnerTube player response contain an
// UNTRANSFORMED "n" parameter. Google Video throttles every stream whose n
// parameter was not deciphered by the player's JavaScript to roughly
// 40-80 kB/s. That is fine for realtime playback (a 160 kbps Opus stream
// needs ~20 kB/s) but hopeless for downloading a 5 MB track in reasonable
// time. YoutubeExplode deciphers the n parameter internally, so URLs taken
// from its manifest download at full connection speed.
//
// WHAT IT DOES
//   1. Gets a deciphered URL from YoutubeExplode (cookie client preferred).
//   2. Downloads it as 4 MiB chunks over 3 parallel HTTP range requests,
//      with per-chunk retries (exponential backoff), a preallocated file and
//      positional (RandomAccess) writes — no torn files, no shared-handle
//      seek races.
//   3. Falls back to the (throttled) playback URL when YoutubeExplode fails,
//      so caching still makes progress instead of doing nothing.
//   4. Keeps its own LRU-capped index, completely independent from the
//      existing TrackCacheService (which keeps working untouched for legacy
//      cached entries).
//
// STORM CONTROL (v4 — persistent queue)
// Only ONE background track downloads at a time (single-flight semaphore).
// Downloads are NO LONGER cancelled when the user skips to another track —
// that cancel-on-skip was why skimmed tracks never showed up in the cache
// (their download died mid-flight and the NEXT track's download then died
// silently while waiting on the gate). Instead, every played track is
// ENQUEUED (Enqueue below) and drained sequentially; the queue survives
// track switches. The queue is capped so swiping through a huge playlist
// does not pile up unbounded work.
// ────────────────────────────────────────────────────────────────────────────

public static class FastTrackDownloader
{
    /// <summary>
    /// Debug.WriteLine is compiled out of release builds; mirror the
    /// important traces through android Log (tag "Musicthingy") so they
    /// are visible with `adb logcat -s Musicthingy` in every build
    /// configuration.
    /// </summary>
    private static void Log(string message)
    {
        Debug.WriteLine($"[FastCache] {message}");
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }

    // ── Configuration ───────────────────────────────────────────────────────
    private const int ChunkSizeBytes = 4 * 1024 * 1024;   // 4 MiB per chunk
    private const int ParallelConnections = 3;            // range requests per track
    private const int MaxChunkRetries = 3;
    private const int ManifestTimeoutSeconds = 90;

    /// <summary>Maximum disk space used by the fast cache (LRU eviction beyond this).</summary>
    public static long CacheLimitBytes { get; set; } = 1024L * 1024 * 1024; // 1 GiB

    // ── HTTP (own pool, separate from the InnerTube client) ─────────────────
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 8,
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    // ── Single-flight gate: one background track downloads at a time ────────
    private static readonly SemaphoreSlim _singleFlight = new(1, 1);

    // ── Persistent background queue (v4) ───────────────────────────────────
    private const int MaxQueueDepth = 64;

    private sealed record CacheRequest(YoutubeClient? Client, string TrackUrl, string? FallbackUrl);

    private static readonly ConcurrentQueue<CacheRequest> _cacheQueue = new();
    private static readonly ConcurrentDictionary<string, byte> _queuedUrls = new();
    private static readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private static Task? _drainLoop;
    private static readonly object _drainLock = new();

    // ── Index (track URL → cached file) ─────────────────────────────────────
    private sealed class CacheEntry
    {
        public string File { get; set; } = "";
        public long Bytes { get; set; }
        public DateTime CachedAtUtc { get; set; }
    }

    private static readonly object _indexLock = new();
    private static Dictionary<string, CacheEntry>? _index;
    private static bool _indexLoaded;

    private static string CacheDir =>
        Path.Combine(FileSystem.CacheDirectory, "fasttracks");

    private static string IndexPath =>
        Path.Combine(CacheDir, "index.json");

    private static void EnsureLoaded()
    {
        if (_indexLoaded) return;
        lock (_indexLock)
        {
            if (_indexLoaded) return;
            try
            {
                if (File.Exists(IndexPath))
                {
                    _index = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(IndexPath))
                             ?? new Dictionary<string, CacheEntry>();
                }
            }
            catch (Exception ex)
            {
                Log($"index load failed: {ex.Message}");
            }
            _index ??= new Dictionary<string, CacheEntry>();
            _indexLoaded = true;
        }
    }

    private static void SaveIndex()
    {
        lock (_indexLock)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_index));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log($"index save failed: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached file path for a track URL, or null when it is not
    /// (or no longer) cached. Safe to call from any thread.
    /// </summary>
    public static string? GetCachedTrackPath(string trackUrl)
    {
        if (string.IsNullOrEmpty(trackUrl)) return null;

        EnsureLoaded();
        lock (_indexLock)
        {
            if (!_index!.TryGetValue(trackUrl, out var entry)) return null;
            string path = Path.Combine(CacheDir, entry.File);
            if (!File.Exists(path))
            {
                _index.Remove(trackUrl); // file vanished (system cache clear) — forget it
                return null;
            }
            return path;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Persistent background queue (v4)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a track for background caching. Fire-and-forget: deduplicated,
    /// skips tracks that are already on disk, and NEVER cancelled by track
    /// switches — the drain loop below keeps working while the user skips
    /// around the playlist. The ViewModel calls this for the current track
    /// AND the next track in the queue (pre-cache, InnerTune-style).
    /// </summary>
    public static void Enqueue(YoutubeClient? yteClient, string trackUrl, string? fallbackDirectUrl)
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

    private static void EnsureDrainLoop()
    {
        lock (_drainLock)
        {
            if (_drainLoop == null || _drainLoop.IsCompleted)
                _drainLoop = Task.Run(DrainCacheQueueAsync);
        }
    }

    private static async Task DrainCacheQueueAsync()
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
                await DownloadTrackAsync(req.Client, req.TrackUrl, req.FallbackUrl, CancellationToken.None);
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
    /// Downloads a track into the fast cache. Returns true when the track is
    /// cached afterwards (freshly downloaded or already present).
    /// </summary>
    /// <param name="yteClient">
    /// YoutubeExplode client — the cookie client when available (YoutubeExplode
    /// deciphers the n parameter, so its URLs download at full speed).
    /// </param>
    /// <param name="trackUrl">Original track URL (the cache key).</param>
    /// <param name="fallbackDirectUrl">
    /// The playback stream URL from the InnerTube resolver — used only when
    /// YoutubeExplode failed. It is n-throttled (~50-80 kB/s) but still works.
    /// </param>
    public static async Task<bool> DownloadTrackAsync(
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
            string ext = ".bin";

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
                        ext = best.Container.Name.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".m4a";
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
                    Log("hint: update the YoutubeExplode NuGet package to the latest release (6.5.7 ANDROID_VR bypasses the PO-token requirement, 6.6.2 VISIONOS agent, 6.6.3 regenerates the SOCS consent cookie with the current date)");
                }
            }

            // ── 2. Fallback: the playback URL (throttled but functional) ────
            // The n-throttle (~40-80 kB/s) is PER CONNECTION, so this
            // fallback also goes through the parallel-range engine once
            // the size is known (3 connections ≈ 3× throughput). It runs
            // on a DETACHED token with a hard 5-minute deadline: it used
            // to be bound to the per-track cancel token, so skipping
            // tracks killed it instantly — a throttled download could
            // never finish, which looked like "nothing is cached".
            bool usingFallback = false;
            CancellationToken downloadCt = ct;

            if (string.IsNullOrEmpty(streamUrl) && !string.IsNullOrEmpty(fallbackDirectUrl))
            {
                streamUrl = fallbackDirectUrl;
                usingFallback = true;
                fallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                downloadCt = fallbackCts.Token;
                Log("using playback URL fallback (throttled; skip-proof with 5-min deadline)");
            }

            if (string.IsNullOrEmpty(streamUrl)) return false;

            // ── 3. Download ──────────────────────────────────────────────────
            Directory.CreateDirectory(CacheDir);
            string baseName = Sha256Hex(trackUrl)[..24] + ext;
            string partPath = Path.Combine(CacheDir, baseName + ".part");
            string finalPath = Path.Combine(CacheDir, baseName);

            try
            {
                // Fallback with unknown size: probe once (Range: bytes=0-0
                // → Content-Range total / Content-Length) so the parallel
                // chunk engine can be used and the extension can be
                // derived from the real Content-Type instead of the
                // hardcoded ".m4a" (opus/webm fallbacks were mislabelled).
                if (usingFallback && totalBytes <= 0)
                {
                    (long size, string contentType) = await ProbeAsync(streamUrl, downloadCt);
                    if (size > 0)
                    {
                        totalBytes = size;
                        Log($"fallback size probe: {size} bytes ({contentType})");
                    }
                    if (contentType.Contains("webm", StringComparison.OrdinalIgnoreCase))
                        ext = ".webm";
                    else if (contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                             contentType.Contains("aac", StringComparison.OrdinalIgnoreCase))
                        ext = ".m4a";
                }

                if (totalBytes > 0)
                {
                    await DownloadParallelChunksAsync(streamUrl, partPath, totalBytes, downloadCt);
                }
                else
                {
                    await DownloadSequentialAsync(streamUrl, partPath, downloadCt);
                }

                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(partPath, finalPath);

                lock (_indexLock)
                {
                    _index![trackUrl] = new CacheEntry
                    {
                        File = baseName,
                        Bytes = totalBytes > 0 ? totalBytes : new FileInfo(finalPath).Length,
                        CachedAtUtc = DateTime.UtcNow
                    };
                }
                SaveIndex();
                PruneIfNeeded();
                Log($"cached {new FileInfo(finalPath).Length} bytes: {trackUrl}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"download failed for {trackUrl}: {ex.Message}");
                TryDelete(partPath);
                throw;
            }
        }
        finally
        {
            fallbackCts?.Dispose();
            _singleFlight.Release();
        }
    }

    /// <summary>Deletes every fast-cache file and the index.</summary>
    public static Task ClearAsync()
    {
        try
        {
            if (Directory.Exists(CacheDir))
                Directory.Delete(CacheDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"clear failed: {ex.Message}");
        }
        lock (_indexLock)
        {
            _index = new Dictionary<string, CacheEntry>();
            _indexLoaded = true;
        }
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Download engines
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task DownloadParallelChunksAsync(
        string url, string partPath, long totalBytes, CancellationToken ct)
    {
        // Preallocate the file so parallel writers can use positional writes.
        using SafeFileHandle handle = File.OpenHandle(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None);
        RandomAccess.SetLength(handle, totalBytes);

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
                await DownloadChunkWithRetryAsync(url, chunk.Start, chunk.End, totalBytes, handle, ct);
            }
            finally
            {
                gate.Release();
            }
        }, ct));

        await Task.WhenAll(tasks);
    }

    private static async Task DownloadChunkWithRetryAsync(
        string url, long start, long end, long totalBytes, SafeFileHandle handle, CancellationToken ct)
    {
        long expected = end - start + 1;

        for (int attempt = 1; attempt <= MaxChunkRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using HttpResponseMessage response = await Http.SendAsync(
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

                    // A 200 body would deliver bytes from position 0 — reject.
                    if (!isFullFile && received > expected)
                        throw new IOException("server ignored Range header");
                }

                if (received != expected)
                    throw new IOException($"short chunk read {received}/{expected} ({start}-{end})");

                return; // chunk complete
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < MaxChunkRetries)
            {
                int backoffMs = 500 * attempt * attempt; // 0.5s → 2s → 4.5s
                Log($"chunk {start}-{end} attempt {attempt} failed: {ex.Message} — retry in {backoffMs}ms");
                await Task.Delay(backoffMs, ct);
            }
        }
    }

    /// <summary>
    /// Probes a stream URL for its total size and content type.
    /// Uses "Range: bytes=0-0" so a 206 answer carries Content-Range
    /// (bytes 0-0/TOTAL); a server that ignores ranges answers 200 and
    /// Content-Length instead. Never throws.
    /// </summary>
    private static async Task<(long Total, string ContentType)> ProbeAsync(
        string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await Http.SendAsync(
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

    private static async Task DownloadSequentialAsync(
        string url, string partPath, CancellationToken ct)
    {
        // Unknown size (playback-URL fallback): plain streaming copy.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await src.CopyToAsync(dst, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Housekeeping
    // ─────────────────────────────────────────────────────────────────────────

    private static void PruneIfNeeded()
    {
        EnsureLoaded();
        bool pruned = false;
        lock (_indexLock)
        {
            while (true)
            {
                long total = _index!.Values.Sum(e => e.Bytes);
                if (total <= CacheLimitBytes || _index.Count == 0) break;

                var oldest = _index.OrderBy(kv => kv.Value.CachedAtUtc).First();
                TryDelete(Path.Combine(CacheDir, oldest.Value.File));
                _index.Remove(oldest.Key);
                pruned = true;
                Log($"pruned '{oldest.Key}' (LRU, limit {CacheLimitBytes} bytes)");
            }
        }
        if (pruned) SaveIndex();
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

    private static string Sha256Hex(string input)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
