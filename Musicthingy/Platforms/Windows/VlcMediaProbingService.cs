#if WINDOWS
using System.Diagnostics;
using Musicthingy.Services;
using LibVLCSharp.Shared;

namespace Musicthingy.Platforms.Windows;

public class VlcMediaProbingService : IMediaProbingService
{
    private const int ParseDelayTimeoutMs = 5000;
    private static LibVLC? _probeVlc;

    public VlcMediaProbingService()
    {
        if (_probeVlc == null)
        {
            _probeVlc = new LibVLC("--no-video", "--no-audio", "--network-caching=500");
        }
    }

    public async Task<TimeSpan?> ProbeMediaDurationAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            if (_probeVlc == null) return null;

            using var media = new Media(_probeVlc, new Uri(url));
            var parseTask = media.Parse(MediaParseOptions.ParseNetwork);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(ParseDelayTimeoutMs, delayCts.Token);

            var completedTask = await Task.WhenAny(parseTask, delayTask);

            if (completedTask == parseTask)
            {
                delayCts.Cancel();
                if (media.Duration > 0) return TimeSpan.FromMilliseconds(media.Duration);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Probe Windows] LibVLC Error: {ex.Message}");
        }
        return null;
    }
}
#endif