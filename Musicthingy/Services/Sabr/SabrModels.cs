using System.Collections.Concurrent;
using System.Diagnostics;

namespace Musicthingy.Services.Sabr;

// ────────────────────────────────────────────────────────────────────────────
// SABR models, the in-memory stream buffer and the session registry.
//
// A SABR "session" plays ONE video's chosen AUDIO format through YouTube's
// server-side streaming: the client repeatedly POSTs a VideoPlaybackAbrRequest
// protobuf to the signed serverAbrStreamingUrl and receives a UMP stream of
// init + media segments, which are appended to SabrStreamBuffer in arrival
// order — producing byte-for-byte the same file yt-dlp's SABR downloader
// writes to disk (init segment followed by the media segments of the format).
//
// SabrStreamRegistry maps short-lived "sabr://<guid>" URIs (what the playback
// service receives from the resolver) to live sessions, keeps at most a
// handful of them for instant replay/seeking, and exposes their [SABR] log
// lines for the playback-error dialog.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>A YouTube format identity: itag + lastModified + xtags (the proto FormatId).</summary>
public sealed record SabrFormatId(int Itag, ulong LastModified, string? Xtags)
{
    public string Key => $"{Itag}:{LastModified}:{Xtags ?? ""}";

    internal void WriteTo(SabrWriter w)
    {
        w.VarintField(1, Itag);
        if (LastModified != 0) w.VarintField(2, unchecked((long)LastModified));
        if (!string.IsNullOrEmpty(Xtags)) w.StringField(3, Xtags);
    }
}

/// <summary>
/// One audio format offered by a SABR-only player response, in the resolver's
/// preference order (opus first, then m4a; higher bitrate first).
/// </summary>
public sealed record SabrAudioFormat(int Itag, ulong LastModified, string? Xtags, string MimeType, int Bitrate)
{
    public SabrFormatId ToFormatId() => new(Itag, LastModified, Xtags);
}

/// <summary>
/// The InnerTube client identity a SABR request must mirror — the same client
/// that produced the serverAbrStreamingUrl (proto ClientInfo inside
/// StreamerContext). ClientName is the numeric InnerTube clientName enum value
/// (ANDROID = 3, WEB = 1, WEB_EMBEDDED_PLAYER = 56, ...).
/// </summary>
public sealed record SabrClientInfo(
    int ClientName,
    string ClientVersion,
    string? OsName = null,
    string? OsVersion = null,
    string? DeviceMake = null,
    string? DeviceModel = null,
    int? AndroidSdkVersion = null,
    string Hl = "en",
    string Gl = "US",
    string? VisitorData = null,
    string? UserAgent = null);

/// <summary>
/// Everything needed to start a SABR streaming session for one video:
/// captured by the resolver from a SABR-only player response.
/// </summary>
public sealed record SabrSessionConfig(
    string VideoId,
    string ServerAbrStreamingUrl,
    string UstreamerConfigB64,
    SabrClientInfo ClientInfo,
    IReadOnlyList<SabrAudioFormat> PreferredAudio,
    string? PoToken = null,
    long StartTimeMs = 0);

/// <summary>An error raised by the SABR protocol client (message is log-friendly).</summary>
public sealed class SabrStreamException : Exception
{
    public SabrStreamException(string message) : base(message) { }
}

// ────────────────────────────────────────────────────────────────────────────
// SabrStreamBuffer — producer/consumer byte buffer.
//
// The session's request loop appends segment bytes; the ExoPlayer DataSource
// reads them from any position (ExoPlayer re-opens the source at computed byte
// positions when seeking, and everything received so far stays in memory, so
// backward seeks are served instantly). Reads block until data, EOF or error.
// ────────────────────────────────────────────────────────────────────────────
public sealed class SabrStreamBuffer
{
    private const long MaxTotalLength = 128 * 1024 * 1024; // hard memory cap

    private readonly object _sync = new();
    private readonly List<byte[]> _chunks = new();
    private long[] _chunkStarts = Array.Empty<long>(); // cumulative offsets, rebuilt lazily
    private bool _offsetsDirty = true;
    private long _totalLength;
    private bool _eof;
    private Exception? _error;

    public long TotalLength
    {
        get { lock (_sync) return _totalLength; }
    }

    public bool IsEof
    {
        get { lock (_sync) return _eof; }
    }

    public void Append(byte[] data)
    {
        lock (_sync)
        {
            if (_eof || _error != null) return; // session already over; drop
            if (data.Length == 0) return;       // zero-length chunks would break the chunk index
            if (_totalLength + data.Length > MaxTotalLength)
            {
                _error = new SabrStreamException($"SABR buffer exceeded the {MaxTotalLength / (1024 * 1024)} MB memory cap");
                Monitor.PulseAll(_sync);
                return;
            }
            _chunks.Add(data);
            _totalLength += data.Length;
            _offsetsDirty = true;
            Monitor.PulseAll(_sync);
        }
    }

    public void SetEof()
    {
        lock (_sync)
        {
            _eof = true;
            Monitor.PulseAll(_sync);
        }
    }

    public void SetError(Exception error)
    {
        lock (_sync)
        {
            if (_error == null)
            {
                _error = error;
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <summary>
    /// True once at least <paramref name="minBytes"/> bytes exist at
    /// <paramref name="position"/> (or the stream already reached EOF there).
    /// False on timeout. Throws the session error if the session failed.
    /// </summary>
    public bool WaitForData(long position, int minBytes, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_sync)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_error != null) throw _error;
                if (_totalLength >= position + Math.Max(1, minBytes)) return true;
                if (_eof) return _totalLength > position;

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return _totalLength > position;
                Monitor.Wait(_sync, (int)Math.Min(remaining, 500));
            }
        }
    }

    /// <summary>
    /// Blocking read at an absolute position. Returns the number of bytes read
    /// (1..count), 0 at end of stream. Throws the session error on failure.
    /// </summary>
    public int Read(long position, byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            while (true)
            {
                if (_error != null) throw _error;
                if (_totalLength > position)
                {
                    int available = (int)Math.Min(count, _totalLength - position);
                    ReadLocked(position, buffer, offset, available);
                    return available;
                }
                if (_eof) return 0;
                Monitor.Wait(_sync, 500);
            }
        }
    }

    private void ReadLocked(long position, byte[] buffer, int offset, int count)
    {
        if (_offsetsDirty)
        {
            _chunkStarts = new long[_chunks.Count];
            long start = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                _chunkStarts[i] = start;
                start += _chunks[i].Length;
            }
            _offsetsDirty = false;
        }

        long pos = position;
        int chunk = FindChunkLocked(pos);
        int written = 0;
        while (written < count)
        {
            byte[] src = _chunks[chunk];
            long chunkStart = _chunkStarts[chunk];
            int within = (int)(pos - chunkStart);
            int take = Math.Min(count - written, src.Length - within);
            Array.Copy(src, within, buffer, offset + written, take);
            written += take;
            pos += take;
            chunk++;
        }
    }

    private int FindChunkLocked(long position)
    {
        // Binary search over the cumulative chunk offsets.
        int lo = 0, hi = _chunkStarts.Length - 1, result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_chunkStarts[mid] <= position) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }
}

// ────────────────────────────────────────────────────────────────────────────
// SabrStreamRegistry — sabr://<guid> → live session.
//
// The resolver registers a session per video (replayed by videoId), the
// playback service receives only the opaque URI. At most MaxSessions sessions
// are retained (each holds the track's bytes for instant replay/seeking);
// sessions that a DataSource currently has open are never evicted.
// ────────────────────────────────────────────────────────────────────────────
public static class SabrStreamRegistry
{
    private const int MaxSessions = 3;
    private const int MaxLogLines = 80;

    private static readonly object _sync = new();
    private static readonly LinkedList<SabrStreamSession> _sessions = new(); // most recently used first
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> _lastLogs = new();

    public static string MakeUri(SabrStreamSession session) => $"sabr://{session.Id}";

    /// <summary>
    /// Looks a session up by its "sabr://&lt;guid&gt;" URI in STRING form
    /// (Media3 hands the player an Android.Net.Uri; the only caller
    /// stringifies it, so shared code stays free of Android types).
    /// Returns null when the id is unknown or the session already failed
    /// (the resolver should re-resolve in that case).
    /// </summary>
    public static SabrStreamSession? Find(string? uri)
    {
        if (string.IsNullOrEmpty(uri) ||
            !Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ||
            !parsed.Scheme.Equals("sabr", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Guid.TryParse(parsed.Host, out Guid id) && !Guid.TryParse(parsed.AbsolutePath.Trim('/'), out id))
            return null;

        lock (_sync)
        {
            for (LinkedListNode<SabrStreamSession>? node = _sessions.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == id)
                {
                    if (node.Value.HasFailed) return null;
                    _sessions.Remove(node);
                    _sessions.AddFirst(node);
                    return node.Value;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the live (non-failed) session for this video, or starts a new
    /// one from the given config. A failed previous session is replaced.
    /// </summary>
    public static SabrStreamSession GetOrCreate(SabrSessionConfig config)
    {
        lock (_sync)
        {
            for (LinkedListNode<SabrStreamSession>? node = _sessions.First; node is not null; node = node.Next)
            {
                if (node.Value.VideoId != config.VideoId) continue;
                if (node.Value.HasFailed)
                {
                    node.Value.Cancel();
                    _sessions.Remove(node);
                    break;
                }
                _sessions.Remove(node);
                _sessions.AddFirst(node);
                return node.Value;
            }

            SabrStreamSession session = new(config);
            _sessions.AddFirst(session);
            EvictLocked();
            return session;
        }
    }

    /// <summary>Removes a failed session for a video (after a pre-flight failure).</summary>
    public static void RemoveFailed(string videoId)
    {
        lock (_sync)
        {
            for (LinkedListNode<SabrStreamSession>? node = _sessions.First; node is not null; node = node.Next)
            {
                if (node.Value.VideoId != videoId) continue;
                node.Value.Cancel();
                _sessions.Remove(node);
                return;
            }
        }
    }

    /// <summary>
    /// The [SABR] log lines of the most recent session for a video (kept even
    /// after the session is gone — read by the playback-error dialog).
    /// </summary>
    public static IReadOnlyList<string> GetRecentLog(string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return Array.Empty<string>();
        lock (_sync)
        {
            foreach (SabrStreamSession session in _sessions)
            {
                if (session.VideoId == videoId)
                {
                    IReadOnlyList<string> lines = session.GetLogSnapshot();
                    _lastLogs[videoId] = lines;
                    return lines;
                }
            }
        }
        return _lastLogs.TryGetValue(videoId, out IReadOnlyList<string>? last) ? last : Array.Empty<string>();
    }

    internal static void OnSessionLog(SabrStreamSession session, string line)
    {
        _lastLogs[session.VideoId] = session.GetLogSnapshot();
    }

    private static void EvictLocked()
    {
        // Evict from the least recently used end; never a session a reader
        // currently has open, and never the one just added (it is first).
        while (_sessions.Count > MaxSessions)
        {
            LinkedListNode<SabrStreamSession>? victim = null;
            for (LinkedListNode<SabrStreamSession>? node = _sessions.Last; node is not null; node = node.Previous)
            {
                if (node.Value.OpenReaderCount == 0)
                {
                    victim = node;
                    break;
                }
            }
            if (victim is null) break; // everything is open; keep them all for now
            victim.Value.Cancel();
            _sessions.Remove(victim);
        }
    }
}

/// <summary>
/// [SABR] tracing helper — Debug.WriteLine plus Android logcat in release
/// builds, mirroring the resolver's Trace().
/// </summary>
internal static class SabrTrace
{
    public static void Log(string message)
    {
        Debug.WriteLine(message);
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }
}
