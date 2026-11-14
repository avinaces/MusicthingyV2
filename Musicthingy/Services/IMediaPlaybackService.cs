namespace Musicthingy.Services;

public interface IMediaPlaybackService : IDisposable
{
    event EventHandler<bool>? BufferingChanged;
    event EventHandler? Playing;
    event EventHandler? Paused;
    event EventHandler? Stopped;
    event EventHandler? ErrorEncountered;
    event EventHandler? EndReached;
    event EventHandler<double>? TimeChanged;
    event EventHandler<double>? LengthChanged;

    bool IsPlaying { get; }
    double CurrentTimeMs { get; }
    double LengthMs { get; }
    /// <summary>Position up to which the player has buffered, in milliseconds (0 when unknown).</summary>
    double BufferedPositionMs { get; }
    string? CurrentMediaUrl { get; }

    void Play(string streamUrl, string userAgent, double startTimeMs);
    void Play(); // Resume
    void Pause();
    void Stop();
    void Seek(double timeMs);
    bool CanReuseMedia(string newUrl);
}