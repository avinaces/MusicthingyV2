#if ANDROID
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using Musicthingy.Services;
using System.Timers;

namespace Musicthingy.Platforms.Android;

public class ExoPlayerMediaPlaybackService : Java.Lang.Object, IMediaPlaybackService, IPlayerListener
{
    private const int StateIdle = 1;
    private const int StateBuffering = 2;
    private const int StateReady = 3;
    private const int StateEnded = 4;

    private readonly LocalStreamProxy _streamProxy;
    private AndroidX.Media3.ExoPlayer.IExoPlayer? _player;
    private System.Timers.Timer? _progressTimer;

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

    public ExoPlayerMediaPlaybackService(LocalStreamProxy streamProxy)
    {
        _streamProxy = streamProxy;

        // Ensure initialization happens safely on the main thread
        MainThread.BeginInvokeOnMainThread(InitializePlayer);
    }

    private void InitializePlayer()
    {
        _streamProxy.Start();

        var context = global::Android.App.Application.Context;

        _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context).Build();
        _player!.AddListener(this);

        // Run timer faster than ViewModel's 500ms debounce to avoid skipping ticks
        _progressTimer = new System.Timers.Timer(250);
        _progressTimer.Elapsed += (s, e) =>
        {
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

        // BYPASS PROXY: Use streamUrl directly instead of _streamProxy.GetProxyUrl(streamUrl)
        // ExoPlayer doesn't get blocked by ISP's or youtube's anti-bot measures, so the proxy is unnecessary and causes extra overhead.
        var dataSourceFactory = new DefaultHttpDataSource.Factory();
        dataSourceFactory.SetUserAgent(userAgent);
        dataSourceFactory.SetAllowCrossProtocolRedirects(true);

        var mediaItem = MediaItem.FromUri(global::Android.Net.Uri.Parse(streamUrl)!);
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