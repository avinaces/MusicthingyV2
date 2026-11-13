#if ANDROID
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using Musicthingy.Services.Sabr;

namespace Musicthingy.Platforms.Android;

// ────────────────────────────────────────────────────────────────────────────
// SabrDataSource — a Media3 DataSource for YouTube's SABR streaming protocol.
//
// SABR-only videos return no direct stream URLs; the resolver instead
// registers a SabrStreamSession (the UMP-speaking client in Services/Sabr/)
// and hands the player an opaque "sabr://<guid>" URI. This DataSource serves
// the session's in-memory byte stream (init segment + media segments, in
// arrival order — byte-for-byte the file yt-dlp's SABR downloader writes) to
// ExoPlayer's progressive loading pipeline.
//
// Open/Close are called for the initial load and on every seek; the session
// (and its buffer) outlives individual opens, so seeks into already-received
// bytes are served instantly and forward seeks wait for the stream to catch
// up. Reads block on ExoPlayer's loading thread — exactly what DefaultHttp-
// DataSource does on a slow network.
//
// Sessions live in SabrStreamRegistry (max 3, readers never evicted) — see
// Services/Sabr/SabrModels.cs.
// ────────────────────────────────────────────────────────────────────────────
public class SabrDataSource : BaseDataSource
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(45);

    private SabrStreamSession? _session;
    // global:: is REQUIRED: this file lives in namespace Musicthingy.Platforms.Android,
    // where an unqualified Android Uri would resolve as Musicthingy.Platforms.Android.Net
    // (CS0234). The ExoPlayer service in the same namespace uses global:: too.
    private global::Android.Net.Uri? _uri;
    private long _position;
    private bool _opened;

    public SabrDataSource() : base(isNetwork: true)
    {
    }

    // The binding declares open(DataSpec? p0) — the param is NULLABLE (no
    // Java @NonNull annotation; the binding's method-level NullableContext(2)
    // marks unannotated references nullable). The override must match or the
    // compiler raises CS8765. Read(byte[]? ...) below is nullable for the
    // same reason.
    public override long Open(DataSpec? dataSpec)
    {
        if (dataSpec is null)
            throw new Java.IO.IOException("SABR data source opened without a DataSpec");

        _uri = dataSpec.Uri;

        // The registry maps "sabr://<guid>" STRINGS to sessions (shared code,
        // no Android types); Media3 hands us an Android Uri, so pass its
        // string form.
        SabrStreamSession? session = SabrStreamRegistry.Find(dataSpec.Uri?.ToString());
        if (session is null)
            throw new Java.IO.IOException(
                $"Unknown SABR session for {dataSpec.Uri} (unknown, evicted or failed — replay the track)");

        TransferInitializing(dataSpec);

        session.EnsureRunning();
        session.AcquireReader();
        _session = session;
        _position = dataSpec.Position;

        try
        {
            // Block until the first byte at the requested position arrives.
            // False (timeout / stream ended before this position) is fatal:
            // there is nothing to read there.
            bool hasData = session.Buffer.WaitForData(_position, 1, OpenTimeout, CancellationToken.None);
            if (!hasData)
                throw new Java.IO.IOException(
                    $"SABR stream did not deliver data at position {_position} " +
                    $"(streamed {session.Buffer.TotalLength} bytes)");
        }
        catch (Java.IO.IOException)
        {
            ReleaseReader();
            throw;
        }
        catch (Exception ex)
        {
            ReleaseReader();
            throw new Java.IO.IOException($"SABR stream failed to open: {ex.Message}");
        }

        _opened = true;
        TransferStarted(dataSpec);
        return C.LengthUnset; // total length is unknown while the stream grows
    }

    public override int Read(byte[]? buffer, int offset, int readLength)
    {
        if (readLength == 0) return 0;
        if (buffer is null)
            throw new Java.IO.IOException("SABR data source read into a null buffer");

        SabrStreamSession? session = _session;
        if (session is null)
            throw new Java.IO.IOException("SABR data source is not open");

        int read;
        try
        {
            read = session.Buffer.Read(_position, buffer, offset, readLength);
        }
        catch (Exception ex)
        {
            throw new Java.IO.IOException($"SABR stream failed: {ex.Message}");
        }

        if (read <= 0)
            return C.ResultEndOfInput;

        _position += read;
        BytesTransferred(read);
        return read;
    }

    public override global::Android.Net.Uri? Uri => _uri;

    public override void Close()
    {
        _uri = null;
        if (!_opened) return;

        _opened = false;
        ReleaseReader();
        TransferEnded();
    }

    private void ReleaseReader()
    {
        SabrStreamSession? session = _session;
        _session = null;
        session?.ReleaseReader();
    }
}

/// <summary>Creates SabrDataSource instances (AndroidX.Media3 DataSource.Factory).</summary>
public class SabrDataSourceFactory : Java.Lang.Object, IDataSourceFactory
{
    public IDataSource CreateDataSource() => new SabrDataSource();
}
#endif
