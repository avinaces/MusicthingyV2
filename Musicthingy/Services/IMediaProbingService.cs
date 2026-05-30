namespace Musicthingy.Services;

public interface IMediaProbingService
{
    Task<TimeSpan?> ProbeMediaDurationAsync(string url, CancellationToken cancellationToken);
}