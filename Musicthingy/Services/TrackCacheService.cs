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
                string tempPath = finalPath + ".tmp";

                using var response = await _httpClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
                fs.Close();

                // Atomically move the fully downloaded file to mark it as valid
                File.Move(tempPath, finalPath, overwrite: true);

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
            long maxBytes = (maxSetting?.Value ?? 1024) * 1048576L; // MB to Bytes

            var files = new DirectoryInfo(_cacheDir).GetFiles()
                .Where(f => !f.Name.EndsWith(".tmp"))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            long currentSize = files.Sum(f => f.Length);

            foreach (var file in files)
            {
                if (currentSize <= maxBytes) break;

                currentSize -= file.Length;
                file.Delete();
                Debug.WriteLine($"[Cache] Evicted old track: {file.Name}");
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