#if ANDROID
using System.Diagnostics;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Musicthingy.Services;

namespace Musicthingy.Platforms.Android;

public class ExoPlayerMediaProbingService : IMediaProbingService
{
    private const int ParseDelayTimeoutMs = 5000;
    private const int StateIdle = 1;
    private const int StateBuffering = 2;
    private const int StateReady = 3;
    private const int StateEnded = 4;

    public async Task<TimeSpan?> ProbeMediaDurationAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        TaskCompletionSource<TimeSpan?> tcs = new TaskCompletionSource<TimeSpan?>();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        ProbePlayerListener? listener = null;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                global::Android.Content.Context context = global::Android.App.Application.Context;
                IExoPlayer? player = new ExoPlayerBuilder(context).Build();

                if (player == null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                listener = new ProbePlayerListener(player, tcs);
                player.AddListener(listener);

                // Correctly resolve local paths vs. remote web streams
                global::Android.Net.Uri? uri = url.StartsWith("/")
                    ? global::Android.Net.Uri.FromFile(new Java.IO.File(url))
                    : global::Android.Net.Uri.Parse(url);

                if (uri == null)
                {
                    tcs.TrySetResult(null);
                    listener.ReleasePlayer();
                    return;
                }

                MediaItem? mediaItem = MediaItem.FromUri(uri);
                if (mediaItem == null)
                {
                    tcs.TrySetResult(null);
                    listener.ReleasePlayer();
                    return;
                }

                player.SetMediaItem(mediaItem);
                player.Prepare(); // Run a headless prep to read the metadata
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Probe Android] ExoPlayer Init Error: {ex.Message}");
                tcs.TrySetResult(null);
                listener?.ReleasePlayer();
            }
        });

        // Hard timeout and cancellation fallback: ensure player is released if probing hangs
        _ = Task.Delay(ParseDelayTimeoutMs, cts.Token).ContinueWith(_ =>
        {
            if (tcs.TrySetResult(null))
            {
                listener?.ReleasePlayer();
            }
        }, TaskScheduler.Default);

        cts.Token.Register(() =>
        {
            if (tcs.TrySetResult(null))
            {
                listener?.ReleasePlayer();
            }
        });

        return await tcs.Task;
    }

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
            if (playbackState == StateReady)
            {
                long durationMs = _player.Duration;
                if (durationMs > 0 && durationMs != C.TimeUnset)
                {
                    _tcs.TrySetResult(TimeSpan.FromMilliseconds(durationMs));
                }
                else
                {
                    _tcs.TrySetResult(null);
                }

                ReleasePlayer();
            }
            else if (playbackState == StateEnded || playbackState == StateIdle)
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

        public void ReleasePlayer()
        {
            if (_isReleased) return;
            _isReleased = true;

            // Player MUST be released strictly on the Main Thread to prevent JNI lifecycle exceptions
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