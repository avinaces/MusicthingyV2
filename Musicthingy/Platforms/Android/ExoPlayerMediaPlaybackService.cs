#if ANDROID
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using Musicthingy.Services;
using System.Timers;
using System.Threading;

namespace Musicthingy.Platforms.Android;

public class ExoPlayerMediaPlaybackService : Java.Lang.Object, IMediaPlaybackService, IPlayerListener
{
    private const int StateIdle = 1;
    private const int StateBuffering = 2;
    private const int StateReady = 3;
    private const int StateEnded = 4;

    private AndroidX.Media3.ExoPlayer.IExoPlayer? _player;
    private System.Timers.Timer? _progressTimer;
    private long _lastBackgroundTickMs = 0;

    // Main-thread-only player state, mirrored for thread-safe getters.
    // Media3 players must only be accessed on the Main/Application Looper
    // thread - reading IPlayer properties from a background thread throws
    // "Player is accessed on the wrong thread".
    private bool _cachedPlayWhenReady = false;
    private int _cachedPlaybackState = StateIdle;
    private long _cachedPositionMs = 0;
    private long _cachedDurationMs = 0;

    public string? CurrentMediaUrl { get; private set; }

    // Cached snapshots (kept in sync from listener callbacks, which ExoPlayer
    // fires on the Main Thread, and from the main-thread timer) make these
    // getters safe to read from ANY thread. IsPlaying reflects ACTUAL
    // playback (PlayWhenReady is also true while buffering).
    public bool IsPlaying => _player != null && _cachedPlayWhenReady && _cachedPlaybackState == StateReady;

    public double CurrentTimeMs
    {
        get
        {
            if (MainThread.IsMainThread && _player != null)
            {
                long pos = _player.CurrentPosition;
                Interlocked.Exchange(ref _cachedPositionMs, pos);
                return pos;
            }
            return Interlocked.Read(ref _cachedPositionMs);
        }
    }

    public double LengthMs
    {
        get
        {
            if (MainThread.IsMainThread && _player != null)
            {
                long dur = _player.Duration;
                if (dur > 0)
                {
                    Interlocked.Exchange(ref _cachedDurationMs, dur);
                    return dur;
                }
                return 0;
            }
            return Interlocked.Read(ref _cachedDurationMs);
        }
    }

    public event EventHandler<bool>? BufferingChanged;
    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? ErrorEncountered;
    public event EventHandler? EndReached;
    public event EventHandler<double>? TimeChanged;
    public event EventHandler<double>? LengthChanged;

    public ExoPlayerMediaPlaybackService()
    {
        MainThread.BeginInvokeOnMainThread(InitializePlayer);
    }

    private void InitializePlayer()
    {

        var context = global::Android.App.Application.Context;

        _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context).Build();
        _player!.AddListener(this);

        // Let ExoPlayer manage the CPU wake lock natively: it holds a
        // partial wake lock ONLY while PlayWhenReady and buffering/loading,
        // and releases it once audio is actually rendering (the audio HAL
        // takes over). This replaces the app's blanket wake lock that used
        // to block system suspend for entire background listening sessions.
        _player.SetWakeMode(C.WakeModeNetwork);

        // Run timer faster than ViewModel's 500ms debounce to avoid skipping ticks
        _progressTimer = new System.Timers.Timer(250);
        _progressTimer.Elapsed += (s, e) =>
        {
            // While the app is not visible (background / screen off) the
            // in-app position UI (slider, lyrics) cannot be seen, so fire
            // TimeChanged at most once per second instead of 4x. Everything
            // critical keeps working: end-of-track detection (plus ExoPlayer's
            // own StateEnded), the 5s position save, and the OS media session
            // position - which the system advances on its own from
            // playbackState + speed without any push updates.
            if (MainActivity.IsInBackground)
            {
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _lastBackgroundTickMs);
                if (now - last < 1000) return;
                Interlocked.Exchange(ref _lastBackgroundTickMs, now);
            }

            // CRITICAL FIX: ExoPlayer properties MUST be read on the Main Thread.
            // Accessing them directly from the timer's background thread caused a silent exception.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // PlayWhenReady alone is also true while buffering; gate the
                // tick on READY state so ticks reflect actual playback.
                if (_player != null && _player.PlayWhenReady && _player.PlaybackState == StateReady)
                {
                    long pos = _player.CurrentPosition;
                    Interlocked.Exchange(ref _cachedPositionMs, pos);
                    TimeChanged?.Invoke(this, pos);
                }
            });
        };
    }

    public void Play(string streamUrl, string userAgent, double startTimeMs)
    {
        CurrentMediaUrl = streamUrl;

        var context = global::Android.App.Application.Context;

        // v7 (SABR): "sabr://<guid>" URIs resolve to a SabrStreamSession -
        // the UMP-speaking client for SABR-only YouTube videos (no direct
        // stream URLs). SabrDataSource serves the session's in-memory stream
        // (init + media segments - a normal progressive byte stream) to
        // ExoPlayer; every other URI keeps the standard HTTP/file stack.
        bool isSabr = streamUrl.StartsWith("sabr://", StringComparison.OrdinalIgnoreCase);

        AndroidX.Media3.DataSource.IDataSourceFactory baseDataSourceFactory;
        if (isSabr)
        {
            baseDataSourceFactory = new SabrDataSourceFactory();
        }
        else
        {
            var httpDataSourceFactory = new DefaultHttpDataSource.Factory();
            httpDataSourceFactory.SetUserAgent(userAgent);
            httpDataSourceFactory.SetAllowCrossProtocolRedirects(true);
            baseDataSourceFactory = new DefaultDataSource.Factory(context, httpDataSourceFactory);
        }

        global::Android.Net.Uri uri;

        if (streamUrl.StartsWith("http://") || streamUrl.StartsWith("https://") ||
            streamUrl.StartsWith("file://") || streamUrl.StartsWith("content://"))
        {
            // Natively structured URIs
            uri = global::Android.Net.Uri.Parse(streamUrl)!;
        }
        else if (streamUrl.StartsWith("/"))
        {
            // Raw absolute Unix paths (e.g., /storage/emulated/0/Music/...)
            uri = global::Android.Net.Uri.FromFile(new Java.IO.File(streamUrl))!;
        }
        else
        {
            // Fallback for everything else
            uri = global::Android.Net.Uri.Parse(streamUrl)!;
        }

        var mediaItem = MediaItem.FromUri(uri);
        var mediaSource = new AndroidX.Media3.ExoPlayer.Source.DefaultMediaSourceFactory(baseDataSourceFactory)
            .CreateMediaSource(mediaItem);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_player != null)
            {
                _player.SetMediaSource(mediaSource);
                _player.Prepare();

                if (startTimeMs > 0)
                    _player.SeekTo((long)startTimeMs);

                _player.PlayWhenReady = true;
                _cachedPlayWhenReady = true;
            }
        });
    }

    public void Play()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_player != null)
            {
                _player.PlayWhenReady = true;
                _cachedPlayWhenReady = true;
            }
        });
    }

    public void Pause()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_player != null)
            {
                _player.PlayWhenReady = false;
                _cachedPlayWhenReady = false;
            }
        });
    }

    public void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_player != null)
            {
                _player.Stop();
                _cachedPlaybackState = StateIdle;
            }
        });
    }

    public void Seek(double timeMs)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_player != null)
            {
                _player.SeekTo((long)timeMs);
                Interlocked.Exchange(ref _cachedPositionMs, (long)timeMs);
            }
        });
    }

    public bool CanReuseMedia(string newUrl)
    {
        return CurrentMediaUrl == newUrl && _player != null && _cachedPlaybackState != StateIdle;
    }

    // --- Native ExoPlayer Listener Callbacks ---

    public void OnIsPlayingChanged(bool isPlaying)
    {
        if (isPlaying)
        {
            _progressTimer?.Start();
            Playing?.Invoke(this, EventArgs.Empty);

            // Safely fetch length (sometimes it's not ready immediately during StateReady)
            if (_player != null)
            {
                long dur = _player.Duration;
                if (dur > 0)
                {
                    Interlocked.Exchange(ref _cachedDurationMs, dur);
                    LengthChanged?.Invoke(this, dur);
                }
            }
        }
        else
        {
            _progressTimer?.Stop();
            Paused?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OnPlayWhenReadyChanged(bool playWhenReady, int reason)
    {
        _cachedPlayWhenReady = playWhenReady;
    }

    public void OnPlaybackStateChanged(int playbackState)
    {
        _cachedPlaybackState = playbackState;

        switch (playbackState)
        {
            case StateBuffering:
                BufferingChanged?.Invoke(this, true);
                break;
            case StateReady:
                BufferingChanged?.Invoke(this, false);
                if (_player != null)
                {
                    long dur = _player.Duration;
                    if (dur > 0)
                    {
                        Interlocked.Exchange(ref _cachedDurationMs, dur);
                        LengthChanged?.Invoke(this, dur);
                    }
                }
                break;
            case StateEnded:
                EndReached?.Invoke(this, EventArgs.Empty);
                break;
            case StateIdle:
                Stopped?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void OnPlayerError(PlaybackException? error)
    {
        ErrorEncountered?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressTimer?.Stop();
            _progressTimer?.Dispose();

            if (_player != null)
            {
                var p = _player;
                _player = null;
                // CRITICAL FIX: ExoPlayer must also be released on the Main Thread
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    p.Release();
                    p.Dispose();
                });
            }
        }
        base.Dispose(disposing);
    }
}
#endif