using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Helpers;
using Musicthingy.Models;

namespace Musicthingy;

// Registered as a MUSIC PLAYER:
//  - CategoryAppMusic on MAIN makes the app a candidate for "music apps" and
//    for the default-handler picker.
//  - The two ACTION_VIEW filters make it an "Open with" target for audio files
//    (content:// from the file manager / SAF / downloads) and for audio
//    STREAMS (http/https), so a browser link can hand the URL straight over.
//  - SingleTask instead of SingleTop: an intent delivered while the app is
//    running must come back to THIS instance. SingleTop lets the calling app
//    (a file manager) host a second MainActivity inside its own task, which
//    breaks MAUI's single-window host (a second MauiApp / Activity instance).
//    The notification's PendingIntent (SingleTop | ClearTop) still lands in
//    OnNewIntent under SingleTask.
//  - Exported: required for other apps to be able to launch us.
[Activity(
    Theme = "@style/Musicthingy.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryAppMusic, Intent.CategoryDefault })]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeTypes = new[] { "audio/*", "application/ogg", "application/x-ogg", "application/itunes" })]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "http", "https" },
    DataMimeTypes = new[] { "audio/*", "application/ogg", "application/x-ogg", "application/itunes" })]
public class MainActivity : MauiAppCompatActivity
{
    // True while the activity is not visible (app in background or screen off).
    // Used by the playback progress timer to skip work that only matters when
    // the user can actually see it (slider, lyrics highlight).
    public static volatile bool IsInBackground;

    // An intent that arrived before MainViewModel subscribed to the messenger
    // (cold start from another app). Kept here until the ViewModel drains it.
    private static MediaControlMessage? _pendingLaunchMessage;

    /// <summary>
    /// Called by MainViewModel right after it subscribes to the messenger, so a
    /// cold-start VIEW / PLAY_VIDEO intent is never lost. (Delivering it a
    /// moment later is normal: MAUI creates the page/ViewModel after
    /// OnCreate's base call, which is where the intent is handled.)
    /// </summary>
    public static MediaControlMessage? ConsumePendingLaunchMessage()
    {
        MediaControlMessage? pending = _pendingLaunchMessage;
        _pendingLaunchMessage = null;
        return pending;
    }

    protected override void OnPause()
    {
        IsInBackground = true;
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsInBackground = false;

        // Coming back from the background (or from an OEM lock screen) can leave
        // the bar icons in the system's light-mode state - reassert them.
        ForceDarkSystemBars();
    }

    /// <summary>
    /// A UI-mode change (system theme switch, or an OEM overlay) can flip the
    /// status/navigation bar icons back to dark. UiMode is in the Activity's
    /// ConfigurationChanges, so this is called instead of a recreate.
    /// Rotation / foldable posture changes also arrive here (ScreenSize and
    /// SmallestScreenSize are in ConfigurationChanges too) and change the
    /// safe-area insets, so the SafeAreaService cache is dropped to re-read
    /// them on the next layout.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ForceDarkSystemBars();
        Services.SafeAreaService.Invalidate();
    }

    /// <summary>
    /// Keeps the status-bar and navigation-bar icons WHITE (light) for the
    /// app's always-dark UI. On Android 8/9 - and on newer phones while the
    /// system is in light mode - those icons default to BLACK and would be
    /// invisible on the #111214 background. The app theme already sets the
    /// matching attributes; this re-asserts them at runtime because MAUI and
    /// OEM overlays can re-apply the system appearance after the theme is
    /// inflated.
    /// </summary>
    private void ForceDarkSystemBars()
    {
        if (Window == null)
            return;

        WindowInsetsControllerCompat? controller =
            WindowCompat.GetInsetsController(Window, Window.DecorView);
        if (controller == null)
            return;

        // false = light (white) icons, which is what a dark background needs.
        controller.AppearanceLightStatusBars = false;
        controller.AppearanceLightNavigationBars = false;
    }

    // Catches Bluetooth headset clicks and keyboard media keys globally
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ForceDarkSystemBars();
        // OpenTune window policy: edge-to-edge with transparent system bars;
        // MAUI layouts then apply SafeAreaService insets themselves.
        Services.SafeAreaSetup.Apply(this);
        Android.Util.Log.Info("Musicthingy", "[DebugApi] build v9.5-cipherretry loaded");
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

    /// <summary>
    /// Delivers the message now if the ViewModel is listening, otherwise parks
    /// it for <see cref="ConsumePendingLaunchMessage"/>. A message counts as
    /// handled when the receiver sets <see cref="MediaControlMessage.Consumed"/>.
    /// </summary>
    private static void DispatchOrQueue(MediaControlMessage message)
    {
        WeakReferenceMessenger.Default.Send(message);
        if (!message.Consumed)
            _pendingLaunchMessage = message;
    }

    private void HandleIntent(Intent? intent)
    {
        Android.Util.Log.Info("Musicthingy", $"[DebugApi] HandleIntent action={intent?.Action ?? "(null)"}");

        // "Open with Musicthingy" / a browser handing over an audio stream.
        if (intent?.Action == Intent.ActionView && intent.Data is Android.Net.Uri uri)
        {
            var (title, artist, durationMs, artworkPath) = ReadAudioMetadata(uri);
            Android.Util.Log.Info("Musicthingy", $"[External] ACTION_VIEW {uri} title={title ?? "(none)"} artist={artist ?? "(none)"}");
            DispatchOrQueue(new MediaControlMessage
            {
                Action = "PlayUri",
                Uri = uri.ToString(),
                Title = title,
                Artist = artist,
                DurationMs = durationMs,
                ArtworkPath = artworkPath
            });
            return;
        }

        // When clicked from the notification, ask the ViewModel to center the
        // track list on the currently playing track (manual scroll-to-track).
        if (intent?.Action == "MUSIC_ACTION_OPEN_APP")
        {
            DispatchOrQueue(new MediaControlMessage { Action = "ScrollToCurrentTrack" });
        }
        // Debug API for driving playback over adb (see
        // MainViewModel.PlayVideoByIdAsync):
        //   adb shell am start -n <package>/<activity> -a MUSIC_ACTION_PLAY_VIDEO \
        //       --es videoId <id> [--ei startSeconds 55] \
        //       [--es clients tv|sabr|web,mweb] [--es cache off]
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

            // Test-only client selection: replace the fallback chain with
            // exactly these clients ("tv", "sabr", "web", "mweb",
            // "web_embedded", "visionos"; comma-separated). Empty = full chain.
            string? clients = intent.GetStringExtra("clients")?.Trim();
            Musicthingy.Services.YouTubeResolverService.ClientsForTest =
                string.IsNullOrWhiteSpace(clients) ? null : clients;

            // Test-only cache bypass: "off"/"none"/"bypass" (or --ez noCache
            // true) makes both playback and the background downloader ignore
            // the on-disk cache, so the live network path is always exercised.
            string? cache = intent.GetStringExtra("cache")?.Trim();
            bool noCache = intent.GetBooleanExtra("noCache", false) ||
                string.Equals(cache, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cache, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cache, "bypass", StringComparison.OrdinalIgnoreCase);
            Musicthingy.Services.TrackCacheService.BypassCacheForTest = noCache;
            Musicthingy.Services.YouTubeResolverService.BypassUrlCacheForTest = noCache;

            // Test-only SABR diagnostics: --es dump sabr logs the first three
            // UMP bodies (base64) for a diff against a working reference
            // client; --ez sabrNoRn true omits the ?rn= request-number param
            // (the reference request line has none).
            string? dump = intent.GetStringExtra("dump")?.Trim();
            Musicthingy.Services.Sabr.SabrStreamSession.DumpRequests =
                string.Equals(dump, "sabr", StringComparison.OrdinalIgnoreCase);
            Musicthingy.Services.Sabr.SabrStreamSession.OmitRequestNumberForTest =
                intent.GetBooleanExtra("sabrNoRn", false);
            Musicthingy.Services.Sabr.SabrStreamSession.SendUrlPoTokenForTest =
                intent.GetBooleanExtra("sabrUrlPot", false);
            Musicthingy.Services.Sabr.SabrStreamSession.ReferenceHeadersForTest =
                intent.GetBooleanExtra("sabrRefHeaders", false);
            Musicthingy.Services.Sabr.SabrStreamSession.SkipSessionForTest =
                intent.GetBooleanExtra("sabrSkipSession", false);

            Android.Util.Log.Info("Musicthingy", $"[DebugApi] intent MUSIC_ACTION_PLAY_VIDEO {videoId} start={startSeconds} mode={mode ?? "auto"} binding={binding ?? "video"} identity={identity ?? "auto"} clients={clients ?? "all"} cache={(noCache ? "bypass" : "normal")} dump={dump ?? "none"} sabrNoRn={Musicthingy.Services.Sabr.SabrStreamSession.OmitRequestNumberForTest} sabrUrlPot={Musicthingy.Services.Sabr.SabrStreamSession.SendUrlPoTokenForTest}");
            DispatchOrQueue(new MediaControlMessage
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

    /// <summary>
    /// Reads title / artist / duration / embedded artwork for an opened audio
    /// URI. Every step is best effort - a file manager can hand over a URI that
    /// the retriever cannot open, and playback must still be attempted.
    /// </summary>
    private (string? Title, string? Artist, long DurationMs, string? ArtworkPath) ReadAudioMetadata(Android.Net.Uri uri)
    {
        string? title = null;
        string? artist = null;
        long durationMs = 0;
        string? artworkPath = null;

        bool isContent = string.Equals(uri.Scheme, Android.Content.ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase);
        bool isLocal = isContent || string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase);

        // Only local URIs are probed. MediaMetadataRetriever.SetDataSource
        // OPENS the source, so an http(s) stream would be downloaded on the UI
        // thread (this runs from OnCreate/OnNewIntent) - an ANR for a large file.
        if (isLocal)
        {
            try
            {
                using var retriever = new Android.Media.MediaMetadataRetriever();
                retriever.SetDataSource(this, uri);

                title = retriever.ExtractMetadata(Android.Media.MetadataKey.Title);
                artist = retriever.ExtractMetadata(Android.Media.MetadataKey.Artist)
                         ?? retriever.ExtractMetadata(Android.Media.MetadataKey.Albumartist);

                if (long.TryParse(retriever.ExtractMetadata(Android.Media.MetadataKey.Duration), out long duration))
                    durationMs = duration;

                byte[]? artBytes = retriever.GetEmbeddedPicture();
                if (artBytes is { Length: > 0 })
                {
                    // Unique per URI: PlaylistItem's icon cache is keyed by this
                    // path, so a shared name would show the PREVIOUS file's artwork.
                    string artName = $"external_intent_art_{MediaUrlHelper.GetSafeHashId(uri.ToString())[..16]}.jpg";
                    artworkPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, artName);
                    File.WriteAllBytes(artworkPath, artBytes);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Info("Musicthingy", $"[External] metadata read failed: {ex.Message}");
            }
        }

        // Fall back to the display name the document provider reports.
        if (string.IsNullOrWhiteSpace(title) && isContent)
        {
            try
            {
                using var cursor = ContentResolver?.Query(
                    uri,
                    new[] { Android.Provider.IOpenableColumns.DisplayName },
                    null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    int column = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
                    if (column >= 0)
                        title = Path.GetFileNameWithoutExtension(cursor.GetString(column));
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(uri.LastPathSegment);

        if (string.IsNullOrWhiteSpace(title))
            title = uri.Host ?? "Audio Track";

        return (title, artist, durationMs, artworkPath);
    }
}
