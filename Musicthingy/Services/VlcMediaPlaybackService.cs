#if WINDOWS
using LibVLCSharp.Shared;
using System.Diagnostics;

namespace Musicthingy.Services;

public class VlcMediaPlaybackService : IMediaPlaybackService
{
    private readonly LocalStreamProxy _streamProxy;
    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private Media? _currentMedia;

    public string? CurrentMediaUrl { get; private set; }
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public double CurrentTimeMs => _player?.Time ?? 0;
    public double LengthMs => _player?.Length ?? 0;
    // LibVLC does not expose a buffered position; report the playhead so the
    // legacy Windows build keeps a sensible value.
    public double BufferedPositionMs => CurrentTimeMs;

    public event EventHandler<bool>? BufferingChanged;
    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? ErrorEncountered;
    public event EventHandler? EndReached;
    public event EventHandler<double>? TimeChanged;
    public event EventHandler<double>? LengthChanged;

    public VlcMediaPlaybackService(LocalStreamProxy streamProxy)
    {
        _streamProxy = streamProxy;
        InitializeVLCEngine();
    }

    private void InitializeVLCEngine()
    {
        _streamProxy.Start();

        _libVLC = new LibVLC(
            "--no-video",
            "--http-reconnect",
            "--no-ipv6",
            "--verbose=2"
        );

        _player = new MediaPlayer(_libVLC);

        _player.Buffering += (s, e) => BufferingChanged?.Invoke(this, e.Cache < 100f);
        _player.Playing += (s, e) => Playing?.Invoke(this, EventArgs.Empty);
        _player.Paused += (s, e) => Paused?.Invoke(this, EventArgs.Empty);
        _player.Stopped += (s, e) => Stopped?.Invoke(this, EventArgs.Empty);
        _player.EncounteredError += (s, e) => ErrorEncountered?.Invoke(this, EventArgs.Empty);
        _player.EndReached += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
        _player.TimeChanged += (s, e) => TimeChanged?.Invoke(this, e.Time);
        _player.LengthChanged += (s, e) => LengthChanged?.Invoke(this, e.Length);
    }

    public void Play(string streamUrl, string userAgent, double startTimeMs)
    {
        _player?.Stop();

        if (_currentMedia != null)
        {
            try { _currentMedia.Dispose(); } catch { }
            _currentMedia = null;
        }

        CurrentMediaUrl = streamUrl;
        string proxyUrl = _streamProxy.GetProxyUrl(streamUrl);

        _currentMedia = new Media(_libVLC!, new Uri(proxyUrl));
        _currentMedia.AddOption($":http-user-agent={userAgent}");
        _currentMedia.AddOption(":network-caching=2000");
        _currentMedia.AddOption(":clock-jitter=0");
        _currentMedia.AddOption(":clock-synchro=0");

        if (startTimeMs > 0)
        {
            _currentMedia.AddOption($":start-time={startTimeMs / 1000.0}");
        }

        _player?.Play(_currentMedia);
    }

    public void Play() => _player?.Play();
    public void Pause() => _player?.Pause();
    public void Stop() => _player?.Stop();
    public void Seek(double timeMs)
    {
        if (_player != null) _player.Time = (long)timeMs;
    }

    public bool CanReuseMedia(string newUrl)
    {
        return CurrentMediaUrl == newUrl &&
               _player != null &&
               _player.Media != null &&
               _player.State != VLCState.Stopped &&
               _player.State != VLCState.Ended &&
               _player.State != VLCState.Error &&
               _player.State != VLCState.NothingSpecial;
    }

    public void Dispose()
    {
        _currentMedia?.Dispose();
        _player?.Dispose();
        _libVLC?.Dispose();
    }
}
#endif