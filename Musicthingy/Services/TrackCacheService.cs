using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Musicthingy.Settings.Definitions;

namespace Musicthingy.Services;

public class TrackCacheService
{
    private readonly string _cacheDir;
    private readonly HttpClient _httpClient;
    private readonly SettingsManager _settingsManager;
    private readonly ConcurrentDictionary<string, Task> _activeDownloads = new();

    public event EventHandler? CacheUpdated;

    /// <summary>Fires when a download's progress changes (URL, downloaded bytes, total bytes, speed KB/s).</summary>
    public event Action<DownloadProgress>? DownloadProgressChanged;

    public record DownloadProgress(string Url, long DownloadedBytes, long? TotalBytes, double SpeedKBps);

    public bool IsTrackCached(string originalUrl) => !string.IsNullOrEmpty(originalUrl) && GetCachedTrackPath(originalUrl) != null;

    public TrackCacheService(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _cacheDir = Path.Combine(FileSystem.CacheDirectory, "TrackCache");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);

        CleanupTempFiles();
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

    public Task CacheTrackAsync(string originalUrl, string streamUrl)
    {
        string id = GetSafeId(originalUrl);
        string finalPath = Path.Combine(_cacheDir, id);

        if (File.Exists(finalPath)) return Task.CompletedTask;

        // Prevent multiple simultaneous downloads of the same file
        return _activeDownloads.GetOrAdd(id, async _ =>
        {
            try
            {
                string tempPath = finalPath + "." + Guid.NewGuid() + ".tmp";

                using var response = await _httpClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
                using var stream = await response.Content.ReadAsStreamAsync();

                // 64KB buffer — matches the default FileStream buffer but avoids
                // the per-byte overhead of the 8KB default in CopyToAsync.
                byte[] buffer = new byte[65536];
                int read;
                long downloadedBytes = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastReportBytes = 0;
                long lastReportTicks = sw.ElapsedTicks;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read);
                    downloadedBytes += read;

                    // Report progress every 500ms or every 512KB
                    if (sw.ElapsedTicks - lastReportTicks > 5000000 || downloadedBytes - lastReportBytes > 524288)
                    {
                        double elapsedSec = (sw.ElapsedTicks - lastReportTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                        long bytesSinceLastReport = downloadedBytes - lastReportBytes;
                        double speedKBps = elapsedSec > 0 ? bytesSinceLastReport / 1024.0 / elapsedSec : 0;

                        DownloadProgressChanged?.Invoke(new DownloadProgress(originalUrl, downloadedBytes, totalBytes, speedKBps));

                        lastReportBytes = downloadedBytes;
                        lastReportTicks = sw.ElapsedTicks;
                    }
                }

                fs.Close();

                // Atomically move the fully downloaded file to mark it as valid
                File.Move(tempPath, finalPath, overwrite: true);

                // Final progress report (100%)
                DownloadProgressChanged?.Invoke(new DownloadProgress(originalUrl, downloadedBytes, totalBytes, 0));

                EnforceCacheLimit();

                CacheUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cache] Error caching track: {ex.Message}");
            }
            finally
            {
                _activeDownloads.TryRemove(id, out Task? _);
            }
        });
    }

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
}