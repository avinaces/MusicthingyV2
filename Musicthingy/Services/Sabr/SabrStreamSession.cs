using System.Globalization;
using System.Net.Sockets;

namespace Musicthingy.Services.Sabr;

// ────────────────────────────────────────────────────────────────────────────
// SabrStreamSession — a SABR (Server-ABR) streaming client for ONE video's
// chosen AUDIO format. Clean-room C# port of the protocol flow implemented by
// yt-dlp's SABR downloader (yt_dlp/extractor/youtube/_streaming/sabr/, PR
// #13515, coletdjnz) and YouTube.js's googlevideo SabrStream — see
// Docs/SABR_Research.md for the full protocol notes and field tables.
//
// The loop (VOD, audio-only):
//   1. POST a VideoPlaybackAbrRequest protobuf (content-type
//      application/x-protobuf) to the signed serverAbrStreamingUrl, with a
//      fresh `rn` query param per request; accept: application/vnd.yt-ump.
//   2. Parse the UMP response parts: FORMAT_INITIALIZATION_METADATA
//      (register the server's format choice), MEDIA_HEADER / MEDIA /
//      MEDIA_END (init + media segments — appended to the buffer in arrival
//      order, byte-for-byte the file yt-dlp's SABR downloader writes),
//      SABR_CONTEXT_UPDATE (opaque server context — echoed back on the next
//      request), NEXT_REQUEST_POLICY (server pacing), STREAM_PROTECTION_STATUS
//      (PO-token state), SABR_REDIRECT / SABR_ERROR.
//   3. Advance player time to the end of the consumed range, report the
//      buffered ranges on the next request, and stop when the audio format
//      reached its last segment.
//
// Deliberately NOT ported (out of scope for a music player's v1): video
// playback, captions, ad-cuepoint acks, live deep-rewind, resume-from-disk.
// Video/caption tracks the server initializes are marked "fully consumed" so
// the server stops sending them (exactly what yt-dlp does for audio-only).
// ────────────────────────────────────────────────────────────────────────────
public sealed class SabrStreamSession : IDisposable
{
    private const int MaxEmptyRequests = 3;
    private const int MaxHttpRetries = 3;
    private const int MaxPreferredFormats = 3;
    private const long JsMaxSafeInteger = 9007199254740991L;
    private const long BroadcastTargetSegmentDurationMs = 5000;
    private const long BroadcastEstSegmentDurationMs = 4900;

    // The UMP body streams for as long as the server has parts; the shared
    // resolver handler is reused (IPv4-forcing) but without a client timeout.
    private static readonly HttpClient SharedHttp = new(
        YouTubeResolverService.Ipv4Handler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly SabrSessionConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _runSync = new();
    private Task? _runTask;

    // ── Protocol state (owned by the run-loop thread) ──────────────────────
    private readonly Dictionary<string, SabrInitializedFormat> _initializedFormats = new();
    private readonly Dictionary<long, SabrSegmentState> _partialSegments = new();
    private readonly Dictionary<int, byte[]> _sabrContextUpdates = new();
    private readonly HashSet<int> _sabrContextsToSend = new();
    private byte[]? _playbackCookie;
    private int _nextWaitMs;
    private int _requestNumber;
    private string _url;
    private int _emptyRequests;
    private long _playerTimeMs;
    private readonly bool _isBroadcast;
    private bool _loggedLiveOnce;
    // yt-dlp parity (its "ad wait"): the server's backoff only applies when
    // the previous response produced NO new segments. While segments flow,
    // requests go out back-to-back — sleeping between productive requests
    // starved ExoPlayer's buffer (stutters during loading).
    private bool _lastResponseHadSegments;
    // Last STREAM_PROTECTION_STATUS the server sent (0 none, 1 OK,
    // 2 attestation pending, 3 attestation required) — included in the
    // stall error so the playback-error popup shows WHY the stream ended.
    private int _lastProtectionStatus;

    // ── Logging ─────────────────────────────────────────────────────────────
    private readonly object _logSync = new();
    private readonly List<string> _logLines = new();

    public SabrStreamSession(SabrSessionConfig config)
    {
        _config = config;
        // web_creator sometimes serves the URL on c.youtube.com — GVS only.
        _url = config.ServerAbrStreamingUrl.Replace(
            ".c.youtube.com/videoplayback", ".googlevideo.com/videoplayback");
        string? source = GetQueryParam(_url, "source");
        _isBroadcast = source is "yt_live_broadcast" or "yt_premiere_broadcast";
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string VideoId => _config.VideoId;
    public SabrStreamBuffer Buffer { get; } = new();

    public int ActiveItag { get; private set; }

    public bool HasFailed { get; private set; }

    private int _openReaderCount;

    public int OpenReaderCount => Volatile.Read(ref _openReaderCount);

    public void AcquireReader() => Interlocked.Increment(ref _openReaderCount);

    public void ReleaseReader() => Interlocked.Decrement(ref _openReaderCount);

    /// <summary>Starts the request loop (idempotent; at most once per session).</summary>
    public void EnsureRunning()
    {
        lock (_runSync)
        {
            _runTask ??= Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Pre-flight used by the resolver: waits until at least
    /// <paramref name="minBytes"/> bytes arrived (the init segment is
    /// usually the first thing the server sends), the stream ended, or the
    /// session failed. Returns the number of bytes received.
    /// </summary>
    public async Task<long> WaitForFirstBytesAsync(int minBytes, CancellationToken ct)
    {
        EnsureRunning();
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (Buffer.TotalLength >= minBytes)
                return Buffer.TotalLength;

            bool hasData = Buffer.WaitForData(0, minBytes, TimeSpan.FromMilliseconds(500), ct);
            if (hasData && Buffer.TotalLength >= minBytes)
                return Buffer.TotalLength;

            if (Buffer.IsEof)
            {
                return Buffer.TotalLength > 0
                    ? Buffer.TotalLength
                    : throw new SabrStreamException("SABR session ended without any media data");
            }
        }
    }

    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (_logSync) return _logLines.ToArray();
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Main loop
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Log($"[SABR] session starting: video {_config.VideoId}, client {_config.ClientInfo.ClientName} " +
                $"({_config.ClientInfo.ClientVersion}), {_config.PreferredAudio.Count} preferred audio format(s)" +
                (string.IsNullOrEmpty(_config.PoToken) ? ", no PO token" : ", PO token attached"));

            int consecutiveFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                if (_nextWaitMs > 0)
                {
                    // yt-dlp's _check_vod_ad_wait: sleep the server's backoff
                    // ONLY after a response with no new segments AND with SABR
                    // context updates still pending to send. Productive
                    // responses stream at full speed (no sleep).
                    bool contextsPending =
                        _sabrContextsToSend.Any(t => _sabrContextUpdates.ContainsKey(t));
                    if (_lastResponseHadSegments || !contextsPending)
                    {
                        _nextWaitMs = 0;
                    }
                    else
                    {
                        Log($"[SABR] waiting {_nextWaitMs}ms before the next request (server policy)");
                        await Task.Delay(_nextWaitMs, ct).ConfigureAwait(false);
                        _nextWaitMs = 0;
                    }
                }

                CheckUrlExpiry();

                byte[] payload = BuildVpabrRequest();
                _requestNumber++;

                bool gotNewSegments = false;
                try
                {
                    using HttpRequestMessage request = new(HttpMethod.Post,
                        AppendQueryParam(_url, "rn", _requestNumber.ToString(CultureInfo.InvariantCulture)));
                    request.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
                    request.Headers.TryAddWithoutValidation("Accept", "application/vnd.yt-ump");
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    request.Content = new ByteArrayContent(payload);

                    using HttpResponseMessage response = await SharedHttp.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 500 && consecutiveFailures < MaxHttpRetries)
                            throw new HttpRequestException($"HTTP {status} from GVS");
                        throw new SabrStreamException($"GVS returned HTTP {status}");
                    }

                    using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    gotNewSegments = await ParseUmpResponseAsync(body, ct).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    _lastResponseHadSegments = gotNewSegments;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException
                                           && consecutiveFailures < MaxHttpRetries
                                           && !ct.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    Log($"[SABR] request {_requestNumber} failed ({ex.Message}); retrying");
                    await Task.Delay(500 * consecutiveFailures, ct).ConfigureAwait(false);
                    continue;
                }

                UpdatePlayerTime();

                if (CheckEndOfStream())
                {
                    Log($"[SABR] stream complete: {Buffer.TotalLength} bytes, itag {ActiveItag}, " +
                        $"{_requestNumber} request(s)");
                    Buffer.SetEof();
                    return;
                }

                _emptyRequests = gotNewSegments ? 0 : _emptyRequests + 1;
                if (_emptyRequests >= MaxEmptyRequests)
                    throw new SabrStreamException(
                        $"SABR stream stalled: no new segments in {MaxEmptyRequests} consecutive requests " +
                        $"(player time {_playerTimeMs}ms; {ProtectionStatusDescription()})");
            }

            Buffer.SetEof();
        }
        catch (OperationCanceledException)
        {
            Buffer.SetEof();
        }
        catch (Exception ex)
        {
            HasFailed = true;
            Log($"[SABR] error: {ex.Message}");
            Buffer.SetError(ex);
        }
    }

    private void CheckUrlExpiry()
    {
        if (long.TryParse(GetQueryParam(_url, "expire"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long expiresAt) &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt - 30)
        {
            throw new SabrStreamException("GVS session URL expired (re-resolve the track)");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UMP response parsing
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<bool> ParseUmpResponseAsync(Stream body, CancellationToken ct)
    {
        bool newSegments = false;

        // A retried request re-sends in-flight segments from scratch; partial
        // segments from a previous (failed) response must not survive.
        if (_partialSegments.Count > 0)
        {
            Log($"[SABR] discarding {_partialSegments.Count} partial segment(s) from the previous response");
            _partialSegments.Clear();
        }

        UmpPartReader reader = new(body);

        while (true)
        {
            UmpPart? part = await reader.ReadNextAsync(ct).ConfigureAwait(false);
            if (part is null) break;

            switch (part.Type)
            {
                case UmpPart.MediaHeader:
                    HandleMediaHeader(part.Data);
                    break;
                case UmpPart.Media:
                    HandleMediaData(part.Data);
                    break;
                case UmpPart.MediaEnd:
                    if (HandleMediaEnd(part.Data)) newSegments = true;
                    break;
                case UmpPart.FormatInitializationMetadata:
                    HandleFormatInitialization(part.Data);
                    break;
                case UmpPart.SabrContextUpdate:
                    HandleSabrContextUpdate(part.Data);
                    break;
                case UmpPart.SabrContextSendingPolicy:
                    HandleSabrContextSendingPolicy(part.Data);
                    break;
                case UmpPart.StreamProtectionStatus:
                    HandleStreamProtectionStatus(part.Data);
                    break;
                case UmpPart.SabrRedirect:
                    HandleSabrRedirect(part.Data);
                    break;
                case UmpPart.SabrError:
                    HandleSabrError(part.Data);
                    break;
                case UmpPart.NextRequestPolicy:
                    HandleNextRequestPolicy(part.Data);
                    break;
                case UmpPart.LiveMetadata:
                    HandleLiveMetadata(part.Data);
                    break;
                case UmpPart.SabrSeek:
                    HandleSabrSeek(part.Data);
                    break;
                case UmpPart.ReloadPlayerResponse:
                    Log("[SABR] server requested a player-response reload; continuing with the current session URL");
                    break;
                case UmpPart.CuepointList:
                    Log("[SABR] ad cuepoint list received (server-side ad pacing may apply)");
                    break;
                default:
                    // Unknown part types are skipped (the part framing keeps
                    // the stream aligned — skipping is safe).
                    break;
            }
        }

        if (_partialSegments.Count > 0)
        {
            Log($"[SABR] {_partialSegments.Count} partial segment(s) discarded at end of response");
            _partialSegments.Clear();
        }

        return newSegments;
    }

    private void HandleMediaHeader(byte[] data)
    {
        // MediaHeader: 1 header_id(uint32), 2 video_id, 3 itag, 4 last_modified(uint64),
        // 5 xtags, 6 start_data_range, 7 compression, 8 is_init_segment, 9 sequence_number,
        // 10 bitrate_bps, 11 start_ms, 12 duration_ms, 13 format_id, 14 content_length,
        // 15 time_range {1 start_ticks, 2 duration_ticks, 3 timescale}, 16 sequence_lmt
        long headerId = 0, sequenceNumber = 0, startMs = 0, durationMs = 0, contentLength = 0, itag = 0;
        ulong lastModified = 0;
        string? videoId = null, xtags = null;
        bool isInitSegment = false, compressed = false, hasSequence = false,
             hasStartMs = false, hasDurationMs = false, hasContentLength = false;
        long trStartTicks = 0, trDurationTicks = 0;
        int trTimescale = 0;
        bool hasTimeRange = false;
        SabrFormatId? formatId = null;

        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: headerId = e.AsInt64; break;
                case 2: videoId = e.AsString; break;
                case 3: itag = e.AsInt64; break;
                case 4: lastModified = e.AsUInt64; break;
                case 5: xtags = e.AsString; break;
                case 7: compressed = true; break;
                case 8: isInitSegment = e.AsBool; break;
                case 9: sequenceNumber = e.AsInt64; hasSequence = true; break;
                case 11: startMs = e.AsInt64; hasStartMs = true; break;
                case 12: durationMs = e.AsInt64; hasDurationMs = true; break;
                case 13: formatId = ParseFormatId(SabrReader.Of(e)); break;
                case 14: contentLength = e.AsInt64; hasContentLength = true; break;
                case 15:
                {
                    SabrReader tr = SabrReader.Of(e);
                    while (tr.HasNext)
                    {
                        SabrEntry te = tr.Next();
                        switch (te.Field)
                        {
                            case 1: trStartTicks = te.AsInt64; hasTimeRange = true; break;
                            case 2: trDurationTicks = te.AsInt64; hasTimeRange = true; break;
                            case 3: trTimescale = te.AsInt32; break;
                        }
                    }
                    break;
                }
            }
        }

        if (videoId != null && videoId != _config.VideoId)
            throw new SabrStreamException($"received media for another video ({videoId})");

        SabrFormatId fid = formatId ?? new SabrFormatId((int)itag, lastModified, xtags);
        if (!_initializedFormats.TryGetValue(fid.Key, out SabrInitializedFormat? format))
            throw new SabrStreamException($"media header for unknown format {fid.Key}");
        if (compressed)
            throw new SabrStreamException($"compressed media segments are not supported (itag {fid.Itag})");
        if (_partialSegments.ContainsKey(headerId))
            throw new SabrStreamException($"duplicate media header id {headerId}");
        if (!hasSequence && !isInitSegment)
            throw new SabrStreamException($"media header without a sequence number (itag {fid.Itag})");
        if (!isInitSegment && !format.InitSegmentReceived && !format.Discard)
            throw new SabrStreamException($"media segment arrived before the init segment (itag {fid.Itag})");

        bool consumed = false;
        if (!isInitSegment && FindConsumedRange((int)sequenceNumber, format.ConsumedRanges) != null)
            consumed = true;
        if (isInitSegment && format.InitSegmentReceived)
            consumed = true;

        // Strict ordering while not seeking — mirrors yt-dlp's mismatch detection.
        if (!consumed && format.SeekMs == null && !isInitSegment && !format.Discard)
        {
            int? nextExpected = NextExpectedSequence(format);
            if (nextExpected.HasValue && (int)sequenceNumber != nextExpected.Value)
                throw new SabrStreamException(
                    $"out-of-order segment for itag {fid.Itag}: expected {nextExpected.Value}, got {sequenceNumber}");
        }

        long segDurationMs = -1;
        if (hasDurationMs) segDurationMs = durationMs;
        else if (hasTimeRange && trTimescale != 0) segDurationMs = TicksToMs(trDurationTicks, trTimescale) ?? -1;

        if (segDurationMs < 0)
        {
            if (isInitSegment) segDurationMs = 0;
            else if (_isBroadcast) segDurationMs = BroadcastEstSegmentDurationMs;
            else
                throw new SabrStreamException(
                    $"cannot determine the duration of segment {sequenceNumber} (itag {fid.Itag})");
        }

        long segStartMs = 0;
        if (hasStartMs && startMs != 0) segStartMs = startMs;
        else if (hasTimeRange && trTimescale != 0) segStartMs = TicksToMs(trStartTicks, trTimescale) ?? 0;

        _partialSegments[headerId] = new SabrSegmentState
        {
            FormatId = fid,
            Format = format,
            IsInitSegment = isInitSegment,
            SequenceNumber = hasSequence ? (int)sequenceNumber : 0,
            DurationMs = segDurationMs,
            StartMs = segStartMs,
            ContentLength = hasContentLength ? contentLength : -1,
            Consumed = consumed
        };
    }

    private void HandleMediaData(byte[] data)
    {
        // MEDIA part: [header_id: UMP varint][media bytes]
        // Segment data is accumulated per segment and only appended to the
        // stream buffer at MEDIA_END (segment-granularity buffering — a
        // retried request re-sends in-flight segments from scratch, so
        // partial segments must never reach the buffer).
        using MemoryStream ms = new(data, writable: false);
        long headerId = UmpVarint.Read(ms);
        if (headerId < 0)
            throw new SabrStreamException("media part without a header id");
        if (!_partialSegments.TryGetValue(headerId, out SabrSegmentState? segment))
            throw new SabrStreamException($"media data for unknown header id {headerId}");

        long contentLength = data.Length - ms.Position;
        if (contentLength > 0)
        {
            byte[] chunk = new byte[contentLength];
            ms.ReadExactly(chunk, 0, chunk.Length);
            segment.ReceivedDataLength += contentLength;
            segment.DataChunks.Add(chunk);
        }
    }

    private bool HandleMediaEnd(byte[] data)
    {
        // MEDIA_END part: [header_id: UMP varint]
        using MemoryStream ms = new(data, writable: false);
        long headerId = UmpVarint.Read(ms);
        if (!_partialSegments.Remove(headerId, out SabrSegmentState? segment))
            throw new SabrStreamException($"media end for unknown header id {headerId}");

        if (segment.ContentLength >= 0 && segment.ReceivedDataLength != segment.ContentLength)
            throw new SabrStreamException(
                $"segment length mismatch for itag {segment.FormatId.Itag} seq {segment.SequenceNumber}: " +
                $"expected {segment.ContentLength}, got {segment.ReceivedDataLength}");

        // A segment we actually keep is new activity (stall tracking).
        bool isNewSegment = !segment.Consumed;

        if (!segment.Consumed && !segment.Format.Discard)
        {
            foreach (byte[] chunk in segment.DataChunks)
                Buffer.Append(chunk);

            if (segment.IsInitSegment)
                Log($"[SABR] init segment received for itag {segment.FormatId.Itag} " +
                    $"({segment.ReceivedDataLength} bytes)");
        }

        if (segment.IsInitSegment)
        {
            segment.Format.InitSegmentReceived = true;
            return isNewSegment;
        }

        segment.Format.PreviousSegment = segment;
        segment.Format.SeekMs = null;

        if (segment.Consumed)
            return isNewSegment;

        if (segment.Format.Discard)
            return isNewSegment; // no range bookkeeping for discarded tracks

        SabrConsumedRange? range = segment.Format.ConsumedRanges
            .LastOrDefault(r => r.EndSequenceNumber == segment.SequenceNumber - 1);
        if (range != null)
        {
            range.EndSequenceNumber = segment.SequenceNumber;
            range.DurationMs = (segment.StartMs - range.StartTimeMs) + segment.DurationMs;
        }
        else
        {
            segment.Format.ConsumedRanges.Add(new SabrConsumedRange
            {
                StartSequenceNumber = segment.SequenceNumber,
                EndSequenceNumber = segment.SequenceNumber,
                StartTimeMs = segment.StartMs,
                DurationMs = segment.DurationMs
            });
        }

        return isNewSegment;
    }

    private void HandleFormatInitialization(byte[] data)
    {
        // FormatInitializationMetadata: 1 video_id, 2 format_id, 3 end_time_ms,
        // 4 total_segments, 5 mime_type, 9 duration_ticks, 10 duration_timescale
        string? videoId = null, mimeType = null;
        SabrFormatId? formatId = null;
        long endTimeMs = 0, durationTicks = 0;
        int durationTimescale = 0;
        bool hasEndTime = false, hasDuration = false;
        int totalSegments = 0;
        bool hasTotalSegments = false;

        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: videoId = e.AsString; break;
                case 2: formatId = ParseFormatId(SabrReader.Of(e)); break;
                case 3: endTimeMs = e.AsInt64; hasEndTime = true; break;
                case 4: totalSegments = e.AsInt32; hasTotalSegments = true; break;
                case 5: mimeType = e.AsString; break;
                case 9: durationTicks = e.AsInt64; hasDuration = true; break;
                case 10: durationTimescale = e.AsInt32; break;
            }
        }

        if (videoId != null && videoId != _config.VideoId)
            throw new SabrStreamException($"format initialization for another video ({videoId})");
        if (formatId is null)
            throw new SabrStreamException("format initialization without a format id");
        if (_initializedFormats.ContainsKey(formatId.Key))
            return; // already known

        bool isOurs = _config.PreferredAudio.Any(f => f.Itag == formatId.Itag) ||
                      (mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false);

        if (isOurs && _initializedFormats.Values.Any(f => !f.Discard))
            throw new SabrStreamException(
                $"server changed the audio format mid-stream (itag {ActiveItag} → {formatId.Itag})");

        SabrInitializedFormat format = new()
        {
            FormatId = formatId,
            MimeType = mimeType,
            Discard = !isOurs,
            EndTimeMs = hasEndTime ? endTimeMs : null,
            DurationMs = hasDuration ? TicksToMs(durationTicks, durationTimescale) : null,
            LastSegmentNumber = hasTotalSegments ? totalSegments : null
        };

        if (format.Discard)
        {
            // Mark the whole track as consumed so the server stops sending it.
            format.ConsumedRanges.Add(new SabrConsumedRange
            {
                StartSequenceNumber = 0,
                EndSequenceNumber = int.MaxValue,
                StartTimeMs = 0,
                DurationMs = JsMaxSafeInteger
            });
            Log($"[SABR] format itag {formatId.Itag} ({mimeType ?? "?"}) initialized — non-audio track, discarding");
        }
        else
        {
            // VOD starting at 0 always begins at sequence 1.
            if (!_isBroadcast && _config.StartTimeMs == 0)
                format.ExpectedStartSequenceNumber = 1;
            ActiveItag = formatId.Itag;
            Log($"[SABR] audio format itag {formatId.Itag} ({mimeType ?? "?"}) initialized" +
                (format.DurationMs is long dur ? $", duration {dur}ms" : "") +
                (format.LastSegmentNumber is int total ? $", {total} segments" : ""));
        }

        _initializedFormats[formatId.Key] = format;
    }

    private void HandleSabrContextUpdate(byte[] data)
    {
        // SabrContextUpdate: 1 type, 2 scope, 3 value(bytes), 4 send_by_default, 5 write_policy
        int type = 0, writePolicy = 0;
        byte[]? value = null;
        bool sendByDefault = false;

        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: type = e.AsInt32; break;
                case 3: value = e.AsBytes; break;
                case 4: sendByDefault = e.AsBool; break;
                case 5: writePolicy = e.AsInt32; break;
            }
        }

        if (type == 0 || value is null)
            return;

        // write_policy KEEP_EXISTING (2): keep an existing update for this type.
        if (writePolicy == 2 && _sabrContextUpdates.ContainsKey(type))
            return;

        _sabrContextUpdates[type] = value;
        if (sendByDefault)
            _sabrContextsToSend.Add(type);
    }

    private void HandleSabrContextSendingPolicy(byte[] data)
    {
        // SabrContextSendingPolicy: 1 start (repeated int32), 2 stop, 3 discard
        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: _sabrContextsToSend.Add(e.AsInt32); break;
                case 2: _sabrContextsToSend.Remove(e.AsInt32); break;
                case 3:
                    _sabrContextsToSend.Remove(e.AsInt32);
                    _sabrContextUpdates.Remove(e.AsInt32);
                    break;
            }
        }
    }

    private void HandleStreamProtectionStatus(byte[] data)
    {
        // StreamProtectionStatus: 1 status (1 OK, 2 attestation pending, 3 attestation required)
        int status = 0;
        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            if (e.Field == 1) status = e.AsInt32;
        }

        switch (status)
        {
            case 1:
                _lastProtectionStatus = 1;
                Log("[SABR] stream protection OK");
                break;
            case 2:
                _lastProtectionStatus = 2;
                Log(string.IsNullOrEmpty(_config.PoToken)
                    ? "[SABR] stream protection: attestation pending, no PO token attached — continuing"
                    : "[SABR] stream protection: attestation pending for the attached PO token — continuing");
                break;
            case 3:
                _lastProtectionStatus = 3;
                throw new SabrStreamException(string.IsNullOrEmpty(_config.PoToken)
                    ? "stream protection requires a GVS PO token (none attached)"
                    : "stream protection rejected the attached GVS PO token (attestation required)");
            default:
                Log($"[SABR] unknown stream protection status {status}");
                break;
        }
    }

    /// <summary>
    /// Factual description of the last STREAM_PROTECTION_STATUS for the stall
    /// error (shown as-is in the playback-error popup).
    /// </summary>
    private string ProtectionStatusDescription() => _lastProtectionStatus switch
    {
        1 => "stream protection OK",
        2 => string.IsNullOrEmpty(_config.PoToken)
            ? "stream protection: attestation pending, no GVS PO token attached"
            : "stream protection: attestation pending, GVS PO token attached",
        3 => "stream protection: attestation required",
        _ => "no stream protection status received"
    };

    private void HandleSabrRedirect(byte[] data)
    {
        // SabrRedirect: 1 redirect_url
        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            if (e.Field == 1 && !string.IsNullOrEmpty(e.AsString))
            {
                _url = e.AsString;
                Log($"[SABR] redirected to {new Uri(_url).Host}");
            }
        }
    }

    private void HandleSabrError(byte[] data)
    {
        // SabrError: 1 type(string), 2 action, 3 error { 1 status_code, 4 type }
        string? type = null;
        long action = 0, statusCode = 0, errorType = 0;
        bool hasError = false;

        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: type = e.AsString; break;
                case 2: action = e.AsInt64; break;
                case 3:
                {
                    SabrReader err = SabrReader.Of(e);
                    while (err.HasNext)
                    {
                        SabrEntry ee = err.Next();
                        switch (ee.Field)
                        {
                            case 1: statusCode = ee.AsInt64; break;
                            case 4: errorType = ee.AsInt64; break;
                        }
                    }
                    hasError = true;
                    break;
                }
            }
        }

        throw new SabrStreamException(
            $"SABR protocol error from the server: {type ?? "unknown"}" +
            (hasError ? $" (status {statusCode}, error type {errorType}, action {action})" : $" (action {action})"));
    }

    private void HandleNextRequestPolicy(byte[] data)
    {
        // NextRequestPolicy: 4 backoff_time_ms, 7 playback_cookie(bytes)
        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 4:
                    _nextWaitMs = (int)Math.Min(e.AsInt64, 20000);
                    break;
                case 7:
                    _playbackCookie = e.AsBytes;
                    break;
            }
        }
    }

    private void HandleLiveMetadata(byte[] data)
    {
        // LiveMetadata: 3 head_sequence_number, 12/13 min seekable ticks/timescale, 14/15 max
        if (_loggedLiveOnce) return;
        _loggedLiveOnce = true;
        Log("[SABR] live metadata received (live stream pacing)");
    }

    private void HandleSabrSeek(byte[] data)
    {
        // SabrSeek: 1 seek_time_ticks, 2 timescale
        long ticks = 0;
        int timescale = 0;
        SabrReader reader = new(data);
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: ticks = e.AsInt64; break;
                case 2: timescale = e.AsInt32; break;
            }
        }

        long? seekMs = TicksToMs(ticks, timescale);
        if (seekMs is null) return;

        Log($"[SABR] server seek to {seekMs}ms");
        _playerTimeMs = seekMs.Value;
        foreach (SabrInitializedFormat format in _initializedFormats.Values)
            format.SeekMs = seekMs;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Request building (VideoPlaybackAbrRequest)
    // ─────────────────────────────────────────────────────────────────────────

    private byte[] BuildVpabrRequest()
    {
        // VideoPlaybackAbrRequest: 1 client_abr_state, 2 initialized_format_ids,
        // 3 buffered_ranges, 5 video_playback_ustreamer_config, 16/17/18 preferred
        // audio/video/caption format ids, 19 streamer_context
        SabrWriter w = new();

        w.SubMessage(1, cabr =>
        {
            // ClientAbrState: 28 player_time_ms, 40 enabled_track_types_bitfield,
            // 46 drc_enabled, 76 enable_voice_boost, 38 media_capabilities
            cabr.VarintField(28, _playerTimeMs);
            cabr.VarintField(40, 1);          // audio-only track selection
            cabr.BoolField(46, true);         // DRC formats allowed
            cabr.BoolField(76, true);         // voice boost
            if (IncludeMediaCapabilities) WriteMediaCapabilities(cabr);
        });

        foreach (SabrInitializedFormat format in _initializedFormats.Values)
        {
            // Only advertise formats the server fully started (VOD: init or
            // first segment consumed) — otherwise a retry would skip the init.
            if (format.InitSegmentReceived || format.PreviousSegment is not null)
                w.SubMessage(2, f => format.FormatId.WriteTo(f));
        }

        foreach (SabrInitializedFormat format in _initializedFormats.Values)
        {
            foreach (SabrConsumedRange range in format.ConsumedRanges)
            {
                w.SubMessage(3, br =>
                {
                    // BufferedRange: 1 format_id, 2 start_time_ms, 3 duration_ms,
                    // 4 start_segment_index, 5 end_segment_index, 6 time_range
                    br.SubMessage(1, f => format.FormatId.WriteTo(f));
                    br.VarintField(2, range.StartTimeMs);
                    br.VarintField(3, range.DurationMs);
                    br.VarintField(4, range.StartSequenceNumber ?? 0);
                    br.VarintField(5, range.EndSequenceNumber);
                    br.SubMessage(6, tr =>
                    {
                        tr.VarintField(1, range.StartTimeMs);
                        tr.VarintField(2, range.DurationMs);
                        tr.VarintField(3, 1000);
                    });
                });
            }
        }

        byte[] ustreamerConfig = TryDecodeBase64Url(_config.UstreamerConfigB64)
            ?? throw new SabrStreamException("invalid videoPlaybackUstreamerConfig");
        w.BytesField(5, ustreamerConfig);

        foreach (SabrAudioFormat preferred in _config.PreferredAudio.Take(MaxPreferredFormats))
            w.SubMessage(16, f => preferred.ToFormatId().WriteTo(f));

        w.SubMessage(19, sc =>
        {
            // StreamerContext: 1 client_info, 2 po_token, 3 playback_cookie,
            // 5 sabr_contexts, 6 unsent_sabr_contexts
            sc.SubMessage(1, ci =>
            {
                // ClientInfo: 1 hl, 2 gl, 12 device_make, 13 device_model, 14 visitor_data,
                // 15 user_agent, 16 client_name, 17 client_version, 18 os_name, 19 os_version,
                // 64 android_sdk_version
                SabrClientInfo c = _config.ClientInfo;
                ci.StringField(1, c.Hl);
                ci.StringField(2, c.Gl);
                if (c.DeviceMake != null) ci.StringField(12, c.DeviceMake);
                if (c.DeviceModel != null) ci.StringField(13, c.DeviceModel);
                if (!string.IsNullOrEmpty(c.VisitorData)) ci.StringField(14, c.VisitorData);
                if (!string.IsNullOrEmpty(c.UserAgent)) ci.StringField(15, c.UserAgent);
                ci.VarintField(16, c.ClientName);
                ci.StringField(17, c.ClientVersion);
                if (c.OsName != null) ci.StringField(18, c.OsName);
                if (c.OsVersion != null) ci.StringField(19, c.OsVersion);
                if (c.AndroidSdkVersion is int sdk) ci.VarintField(64, sdk);
            });

            if (!string.IsNullOrEmpty(_config.PoToken) &&
                TryDecodeBase64Url(_config.PoToken) is byte[] poToken)
            {
                sc.BytesField(2, poToken);
            }

            if (_playbackCookie is { Length: > 0 } cookie)
                sc.BytesField(3, cookie);

            foreach (int type in _sabrContextsToSend)
            {
                if (_sabrContextUpdates.TryGetValue(type, out byte[]? value))
                {
                    sc.SubMessage(5, ctx =>
                    {
                        ctx.VarintField(1, type);
                        ctx.BytesField(2, value);
                    });
                }
                else
                {
                    sc.VarintField(6, type);
                }
            }
        });

        return w.ToArray();
    }

    private bool IncludeMediaCapabilities =>
        // yt-dlp only sends MediaCapabilities for the ANDROID / IOS family.
        _config.ClientInfo.ClientName is 3 or 5 or 28 or 101;

    private static void WriteMediaCapabilities(SabrWriter cabr)
    {
        // MediaCapabilities: 1 video_format_capabilities (repeated), 5 hdr_mode_bitmask.
        // VideoFormatCapability: 1 video_codec, 2 efficient, 15 is_10_bit_supported.
        cabr.SubMessage(38, caps =>
        {
            for (int codec = 0; codec <= 10; codec++)
            {
                caps.SubMessage(1, vc =>
                {
                    vc.VarintField(1, codec);
                    vc.BoolField(2, true);
                    vc.BoolField(15, true);
                });
            }
            caps.VarintField(5, 0); // HDR disabled
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Stream progression
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdatePlayerTime()
    {
        List<SabrInitializedFormat> active = ActiveFormats();
        if (active.Count == 0) return;
        if (active.Any(f => f.SeekMs != null)) return;

        long minEnd = long.MaxValue;
        foreach (SabrInitializedFormat format in active)
        {
            SabrConsumedRange? range = FindConsumedRangeByTime(_playerTimeMs, format.ConsumedRanges);
            if (range is null) return; // still missing data at the current playhead
            long end = range.StartTimeMs + range.DurationMs;
            if (end < minEnd) minEnd = end;
        }

        if (minEnd != long.MaxValue && minEnd > _playerTimeMs)
            _playerTimeMs = minEnd;
    }

    private bool CheckEndOfStream()
    {
        List<SabrInitializedFormat> active = ActiveFormats();
        if (active.Count == 0) return false;

        foreach (SabrInitializedFormat format in active)
        {
            SabrConsumedRange? range = FindConsumedRangeByTime(_playerTimeMs, format.ConsumedRanges);
            if (range is null) return false;

            bool done =
                (format.LastSegmentNumber is int last && range.EndSequenceNumber >= last) ||
                (format.EndTimeMs is long endTime && _playerTimeMs >= endTime) ||
                (format.DurationMs is long duration && _playerTimeMs >= duration);
            if (!done) return false;
        }
        return true;
    }

    private List<SabrInitializedFormat> ActiveFormats() =>
        _initializedFormats.Values.Where(f => !f.Discard).ToList();

    private static int? NextExpectedSequence(SabrInitializedFormat format)
    {
        if (format.PreviousSegment is SabrSegmentState previous)
            return previous.SequenceNumber + 1;

        if (format.ExpectedStartSequenceNumber is int expected)
        {
            SabrConsumedRange? range = FindConsumedRange(Math.Max(1, expected), format.ConsumedRanges);
            return range is null ? expected : FollowChain(range, format.ConsumedRanges).EndSequenceNumber + 1;
        }

        return null;
    }

    private static SabrConsumedRange? FindConsumedRange(int sequenceNumber, List<SabrConsumedRange> ranges) =>
        ranges.FirstOrDefault(r => r.StartSequenceNumber <= sequenceNumber && sequenceNumber <= r.EndSequenceNumber);

    private static SabrConsumedRange FollowChain(SabrConsumedRange start, List<SabrConsumedRange> ranges)
    {
        SabrConsumedRange current = start;
        bool advanced = true;
        while (advanced)
        {
            advanced = false;
            foreach (SabrConsumedRange next in ranges)
            {
                if (next.StartSequenceNumber == current.EndSequenceNumber + 1)
                {
                    current = next;
                    advanced = true;
                    break;
                }
            }
        }
        return current;
    }

    private static SabrConsumedRange? FindConsumedRangeByTime(long timeMs, List<SabrConsumedRange> ranges)
    {
        foreach (SabrConsumedRange range in ranges.OrderBy(r => r.StartSequenceNumber ?? 0))
        {
            if (range.StartTimeMs <= timeMs && timeMs <= range.StartTimeMs + range.DurationMs)
                return FollowChain(range, ranges);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static SabrFormatId ParseFormatId(SabrReader reader)
    {
        // FormatId: 1 itag, 2 last_modified(uint64), 3 xtags
        int itag = 0;
        ulong lastModified = 0;
        string? xtags = null;
        while (reader.HasNext)
        {
            SabrEntry e = reader.Next();
            switch (e.Field)
            {
                case 1: itag = e.AsInt32; break;
                case 2: lastModified = e.AsUInt64; break;
                case 3: xtags = e.AsString; break;
            }
        }
        return new SabrFormatId(itag, lastModified, xtags);
    }

    private static long? TicksToMs(long ticks, int timescale) =>
        timescale <= 0 ? null : (long)Math.Ceiling(ticks * 1000.0 / timescale);

    private static byte[]? TryDecodeBase64Url(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            string s = value.Trim().Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
        catch
        {
            return null;
        }
    }

    private static string AppendQueryParam(string url, string name, string value) =>
        url + (url.Contains('?') ? '&' : '?') + name + "=" + Uri.EscapeDataString(value);

    private static string? GetQueryParam(string url, string name)
    {
        int queryStart = url.IndexOf('?');
        if (queryStart < 0) return null;
        foreach (string part in url[(queryStart + 1)..].Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    private void Log(string message)
    {
        lock (_logSync)
        {
            _logLines.Add(message);
            if (_logLines.Count > 80)
                _logLines.RemoveRange(0, _logLines.Count - 80);
        }
        SabrTrace.Log(message);
        SabrStreamRegistry.OnSessionLog(this, message);
    }

    // ── Protocol state records ─────────────────────────────────────────────

    private sealed class SabrInitializedFormat
    {
        public required SabrFormatId FormatId { get; init; }
        public bool Discard { get; init; }
        public string? MimeType { get; init; }
        public bool InitSegmentReceived { get; set; }
        public SabrSegmentState? PreviousSegment { get; set; }
        public List<SabrConsumedRange> ConsumedRanges { get; } = new();
        public int? LastSegmentNumber { get; init; }
        public long? EndTimeMs { get; init; }
        public long? DurationMs { get; init; }
        public long? SeekMs { get; set; }
        public int? ExpectedStartSequenceNumber { get; set; }
    }

    private sealed class SabrSegmentState
    {
        public required SabrFormatId FormatId { get; init; }
        public required SabrInitializedFormat Format { get; init; }
        public bool IsInitSegment { get; init; }
        public int SequenceNumber { get; init; }
        public long DurationMs { get; init; }
        public long StartMs { get; init; }
        public long ContentLength { get; init; }
        public bool Consumed { get; init; }
        public long ReceivedDataLength { get; set; }
        public List<byte[]> DataChunks { get; } = new();
    }

    private sealed class SabrConsumedRange
    {
        public int? StartSequenceNumber { get; init; }
        public int EndSequenceNumber { get; set; }
        public long StartTimeMs { get; init; }
        public long DurationMs { get; set; }
    }
}
