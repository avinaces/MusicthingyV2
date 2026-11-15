using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;

namespace Musicthingy;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // True while the activity is not visible (app in background or screen off).
    // Used by the playback progress timer to skip work that only matters when
    // the user can actually see it (slider, lyrics highlight).
    public static volatile bool IsInBackground;

    protected override void OnPause()
    {
        IsInBackground = true;
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsInBackground = false;
    }

    // Catches Bluetooth headset clicks and keyboard media keys globally
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Android.Util.Log.Info("Musicthingy", "[DebugApi] build v9.1-potbinding loaded");
        HandleIntent(Intent);

        // v8.0: the built-in ON-DEVICE GVS PO token generator (a hidden
        // WebView running the youtube.com web player's BotGuard attestation
        // flow — see Platforms/Android/WebViewPoTokenProvider.cs). Registers
        // the mint hook and warms the generator ~3s after boot, so SABR-only
        // tracks stream full-length (and seek) past YouTube's ~60s
        // attestation cutoff without any external server or manual import.
        Platforms.Android.WebViewPoTokenProvider.Initialize();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        Android.Util.Log.Info("Musicthingy", $"[DebugApi] HandleIntent action={intent?.Action ?? "(null)"}");
        // When clicked from the notification, ask the ViewModel to center the
        // track list on the currently playing track (manual scroll-to-track).
        if (intent?.Action == "MUSIC_ACTION_OPEN_APP")
        {
            WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "ScrollToCurrentTrack" });
        }
        // Debug API for driving playback over adb (see
        // MainViewModel.PlayVideoByIdAsync):
        //   adb shell am start -n <package>/<activity> -a MUSIC_ACTION_PLAY_VIDEO \
        //       --es videoId <id> [--ei startSeconds 55]
        else if (intent?.Action == "MUSIC_ACTION_PLAY_VIDEO")
        {
            string? videoId = intent.GetStringExtra("videoId");
            if (string.IsNullOrEmpty(videoId)) return;

            int startSeconds = intent.GetIntExtra("startSeconds", 0);
            string? mode = intent.GetStringExtra("mode");
            Musicthingy.Services.YouTubeResolverService.ForceSabrForTest =
                string.Equals(mode, "sabr", StringComparison.OrdinalIgnoreCase);
            string? binding = intent.GetStringExtra("binding");
            Musicthingy.Services.YouTubeResolverService.UseVisitorBoundTokens =
                string.Equals(binding, "visitor", StringComparison.OrdinalIgnoreCase);
            string? identity = intent.GetStringExtra("identity");
            Musicthingy.Services.YouTubeResolverService.ForceAnonymousForTest =
                string.Equals(identity, "anon", StringComparison.OrdinalIgnoreCase);
            Android.Util.Log.Info("Musicthingy", $"[DebugApi] intent MUSIC_ACTION_PLAY_VIDEO {videoId} start={startSeconds} mode={mode ?? "auto"} binding={binding ?? "video"} identity={identity ?? "auto"}");
            WeakReferenceMessenger.Default.Send(new MediaControlMessage
            {
                Action = "PlayVideo",
                VideoId = videoId,
                StartSeconds = startSeconds > 0 ? startSeconds : null
            });
        }
        // Debug API: point the resolver at a bgutil-ytdlp-pot-provider server
        // (ground-truth control), over adb:
        //   adb shell am start -n <package>/<activity> -a MUSIC_ACTION_SET_POT_PROVIDER \
        //       --es url http://127.0.0.1:4416
        // An empty url clears it. The static is set directly (the persisted
        // setting would be pushed again only on the next settings load).
        else if (intent?.Action == "MUSIC_ACTION_SET_POT_PROVIDER")
        {
            string? url = intent.GetStringExtra("url")?.Trim();
            Musicthingy.Services.YouTubeResolverService.PoTokenProviderUrl =
                string.IsNullOrWhiteSpace(url) ? null : url;
            Android.Util.Log.Info("Musicthingy", $"[DebugApi] PO token provider -> {url ?? "(cleared)"}");
            if (string.IsNullOrWhiteSpace(url)) return;

            // Reachability check: a wrong port/URL must fail loudly HERE, not
            // as a silent "no token" three layers down.
            _ = Task.Run(async () =>
            {
                try
                {
                    using System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
                    using System.Net.Http.HttpResponseMessage resp =
                        await http.GetAsync(url.TrimEnd('/') + "/ping").ConfigureAwait(false);
                    Android.Util.Log.Info("Musicthingy", $"[DebugApi] provider /ping HTTP {(int)resp.StatusCode}");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Info("Musicthingy", $"[DebugApi] provider /ping failed: {ex.Message}");
                }
            });
        }
    }
}