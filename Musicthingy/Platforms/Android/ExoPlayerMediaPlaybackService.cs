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

    public string? CurrentMediaUrl { get; private set; }
    public bool IsPlaying => _player?.PlayWhenReady ?? false;
    public double CurrentTimeMs => _player?.CurrentPosition ?? 0;
    public double LengthMs => _player?.Duration > 0 ? _player.Duration : 0;

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
                if (_player != null && _player.PlayWhenReady)
                {
                    TimeChanged?.Invoke(this, _player.CurrentPosition);
                }
            });
        };
    }

    public void Play(string streamUrl, string userAgent, double startTimeMs)
    {
        CurrentMediaUrl = streamUrl;

        var httpDataSourceFactory = new DefaultHttpDataSource.Factory();
        httpDataSourceFactory.SetUserAgent(userAgent);
        httpDataSourceFactory.SetAllowCrossProtocolRedirects(true);

        var context = global::Android.App.Application.Context;
        var dataSourceFactory = new DefaultDataSource.Factory(context, httpDataSourceFactory);

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
        var mediaSource = new AndroidX.Media3.ExoPlayer.Source.DefaultMediaSourceFactory(dataSourceFactory)
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
            }
        });
    }

    public void Play()
    {
        MainThread.BeginInvokeOnMainThread(() => { if (_player != null) _player.PlayWhenReady = true; });
    }

    public void Pause()
    {
        MainThread.BeginInvokeOnMainThread(() => { if (_player != null) _player.PlayWhenReady = false; });
    }

    public void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() => { if (_player != null) _player.Stop(); });
    }

    public void Seek(double timeMs)
    {
        MainThread.BeginInvokeOnMainThread(() => { if (_player != null) _player.SeekTo((long)timeMs); });
    }

    public bool CanReuseMedia(string newUrl)
    {
        return CurrentMediaUrl == newUrl && _player != null && _player.PlaybackState != StateIdle;
    }

    // --- Native ExoPlayer Listener Callbacks ---

    public void OnIsPlayingChanged(bool isPlaying)
    {
        if (isPlaying)
        {
            _progressTimer?.Start();
            Playing?.Invoke(this, EventArgs.Empty);

            // Safely fetch length (sometimes it's not ready immediately during StateReady)
            if (_player != null && _player.Duration > 0)
                LengthChanged?.Invoke(this, _player.Duration);
        }
        else
        {
            _progressTimer?.Stop();
            Paused?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OnPlaybackStateChanged(int playbackState)
    {
        switch (playbackState)
        {
            case StateBuffering:
                BufferingChanged?.Invoke(this, true);
                break;
            case StateReady:
                BufferingChanged?.Invoke(this, false);
                if (_player != null && _player.Duration > 0)
                    LengthChanged?.Invoke(this, _player.Duration);
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