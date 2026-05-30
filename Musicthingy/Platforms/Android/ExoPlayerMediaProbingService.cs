#if ANDROID
using System.Diagnostics;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Musicthingy.Services;

namespace Musicthingy.Platforms.Android;

public class ExoPlayerMediaProbingService : IMediaProbingService
{
    private const int ParseDelayTimeoutMs = 5000;

    public async Task<TimeSpan?> ProbeMediaDurationAsync(string url, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TimeSpan?>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Cancel via token aborts the completion source
        cts.Token.Register(() => tcs.TrySetResult(null));

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var player = new ExoPlayerBuilder(context).Build();
                var listener = new ProbePlayerListener(player, tcs);
                player.AddListener(listener);

                var mediaItem = MediaItem.FromUri(global::Android.Net.Uri.Parse(url)!);
                player.SetMediaItem(mediaItem);
                player.Prepare(); // Run a headless prep to read the metadata
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Probe Android] ExoPlayer Init Error: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        // Add a safety hard-timeout fallback in case ExoPlayer gets stuck buffering forever
        _ = Task.Delay(ParseDelayTimeoutMs, cts.Token).ContinueWith(t => tcs.TrySetResult(null));

        return await tcs.Task;
    }

    // Standard Object/Listener required for native ExoPlayer bindings
    private class ProbePlayerListener : Java.Lang.Object, IPlayerListener
    {
        private readonly IExoPlayer _player;
        private readonly TaskCompletionSource<TimeSpan?> _tcs;
        private bool _isReleased;

        public ProbePlayerListener(IExoPlayer player, TaskCompletionSource<TimeSpan?> tcs)
        {
            _player = player;
            _tcs = tcs;
        }

        public void OnPlaybackStateChanged(int playbackState)
        {
            // 3 = StateReady, 4 = StateEnded, 1 = StateIdle
            if (playbackState == 3) // StateReady
            {
                var durationMs = _player.Duration;
                if (durationMs > 0 && durationMs != C.TimeUnset)
                    _tcs.TrySetResult(TimeSpan.FromMilliseconds(durationMs));
                else
                    _tcs.TrySetResult(null);

                ReleasePlayer();
            }
            else if (playbackState == 4 || playbackState == 1) // Ended or Idle without Ready state
            {
                _tcs.TrySetResult(null);
                ReleasePlayer();
            }
        }

        public void OnPlayerError(PlaybackException? error)
        {
            Debug.WriteLine($"[Probe Android] ExoPlayer Source Error: {error?.Message}");
            _tcs.TrySetResult(null);
            ReleasePlayer();
        }

        private void ReleasePlayer()
        {
            if (_isReleased) return;
            _isReleased = true;

            // Player MUST be released strictly on Main Thread to prevent JNI crashes
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    _player.RemoveListener(this);
                    _player.Release();
                    _player.Dispose();
                }
                catch { }
            });
        }
    }
}
#endif