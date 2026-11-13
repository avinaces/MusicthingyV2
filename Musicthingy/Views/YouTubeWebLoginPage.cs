using Musicthingy.Services;
using Musicthingy.Services.Accounts;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// YouTubeWebLoginPage — one-tap YouTube sign-in via an embedded WebView.
//
// No more cookie pasting: the user signs in with their Google account inside
// the page and the app harvests EVERYTHING it needs automatically:
//
//   • the full YouTube cookie jar (SID, __Secure-1PSID, __Secure-3PSID,
//     __Secure-*PSIDTS, SAPISID/__Secure-*PAPISID, HSID, SSID, APISID, NID,
//     VISITOR_INFO1_LIVE, YSC, ...) — read from the platform cookie manager
//   • VISITOR_DATA and DATASYNC_ID from window.ytcfg via injected JS
//   • account name / email / channel handle via the innertube
//     account/account_menu endpoint (SAPISIDHASH authorization)
//
// The result is stored as a cookie-kind AccountRecord in the multi-account
// AccountManager (many accounts from different platforms can coexist; each
// platform has one active account).
//
// Flow mirrors InnerTune's LoginScreen.kt (battle-tested in production):
//   1. load Google sign-in with continue=... → music.youtube.com
//   2. detect arrival on https://music.youtube.com
//   3. CookieManager.GetCookie("https://music.youtube.com") (+ .youtube.com)
//   4. evaluate window.ytcfg for VISITOR_DATA / DATASYNC_ID
//   5. account_menu call → account display name
//
// NOTE (v5 — white-screen fix, proven by logcat): v4 assigned
// _webView.Source only in HandlerChanged. On Android the handler fires
// MapSource at CONNECT time — before HandlerChanged runs — and with
// Source still null it loads its own "about:blank" placeholder. v4's
// Navigating handler treated that about:blank as a "non-http navigation"
// and CANCELED it (logcat: "intercepted non-http navigation: about:blank"
// at 21:24:27.106, followed 33 ms later by an already-aborted
// "navigated: …/ServiceLogin…"). Canceling fires StopLoading() and kills
// the REAL sign-in load that was already in flight → permanent white
// screen. Two fixes: (a) assign Source in the constructor so the handler
// loads the sign-in URL directly and the placeholder never happens (safe
// since v4 — the UA is no longer overridden, so nothing needs to happen
// before the first load); (b) never cancel about:/data:/blob:/javascript:
// URIs — they are native WebView URIs, only genuinely non-renderable
// schemes (intent:, market:, mailto:, …) are intercepted.
//
// NOTE (v4, researched against the production apps): Google blocks the
// sign-in when a WebView PRETENDS to be a desktop browser. InnerTune's
// LoginScreen (the most-copied implementation, still working in
// production — Nov 2026) loads this exact URL with the DEFAULT WebView
// user agent and does NOT override it. Android WebView always sends the
// X-Requested-With header (opt-out removal was cancelled by Google — it
// is sent on all requests since 2025 and cannot be removed), so a
// spoofed “Windows Chrome” UA next to that header is a textbook
// “insecure browser” signal — Google let the email through and then
// served “This browser or app may not be secure” at the password step
// (confirmed in the user's logcat). v1-v3 shipped that spoof and got
// blocked; v4 keeps the DEFAULT platform user agent like InnerTune
// (the user's logcat now confirms the live UA is the genuine
// “; wv)” WebView agent). If a specific account still gets blocked
// (Advanced Protection, Workspace policy), the page says so and the
// OAuth device-code flow in Settings remains the fallback and needs no
// browser at all.
// ────────────────────────────────────────────────────────────────────────────

public class YouTubeWebLoginPage : ContentPage
{
    private const string LoginUrl =
        "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// <summary>
    /// User-Agent for the app's own INNER TUBE HTTP calls (the account_menu
    /// identity request). NOT applied to the WebView anymore — see the file
    /// header: the sign-in page must see the DEFAULT platform user agent
    /// (InnerTune parity); a spoofed desktop Chrome UA from an Android
    /// WebView is what Google's “This browser or app may not be secure”
    /// check caught. Keep the major version CURRENT anyway (Google also
    /// refuses outdated UAs on innertube calls): Chrome 158 was stable as of
    /// Nov 2026. Bump this when the account-menu fetch starts failing.
    /// </summary>
    public const string WebViewUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/158.0.0.0 Safari/537.36";

    private readonly IServiceProvider _services;
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebView _webView = null!;
    private Label _status = null!;
    private bool _loaded;
    private bool _captured;
    private bool _uaLogged;
    private string _lastHttpUrl = "";

    private YouTubeWebLoginPage(IServiceProvider services)
    {
        _services = services;

        // ── Build UI in code (no XAML dependency) ───────────────────────────
        var titleLabel = new Label
        {
            Text = "Sign in to YouTube",
            FontAttributes = FontAttributes.Bold,
            FontSize = 18,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
            Margin = new Thickness(16, 12, 0, 12)
        };

        // App icon convention (like PlayerPaneView): ImageButtons with
        // lucide-derived "<lucide-name>_icon.png" resources. Lucide's close
        // glyph is called "x" → x_icon.png. Same geometry as the player's
        // skip buttons (40×40, padding 8, round).
        var closeButton = new ImageButton
        {
            Source = "x_icon.png",
            WidthRequest = 40,
            HeightRequest = 40,
            Padding = 8,
            CornerRadius = 20,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 12, 16, 12)
        };
        closeButton.Clicked += async (s, e) => await CloseAsync();

        _webView = new WebView
        {
            // (v5) Assign the URL HERE, in the constructor. The handler
            // applies Source the moment it connects, so the sign-in page is
            // the FIRST thing it loads — the handler never gets around to its
            // about:blank placeholder (whose interception was the white
            // screen; see the file header). No UserAgent override either —
            // the sign-in page must see the DEFAULT platform user agent
            // (InnerTune parity; a spoofed desktop UA is what got sign-ins
            // blocked in v1-v3).
            Source = new UrlWebViewSource { Url = LoginUrl }
        };

        _status = new Label
        {
            Text = "Sign in with your Google account — the app saves it " +
                   "automatically when the YouTube page opens.",
            FontSize = 12,
            Padding = new Thickness(16, 8, 16, 10),
            LineBreakMode = LineBreakMode.WordWrap
        };

        var header = new Grid();
        header.AddColumnDefinition(new ColumnDefinition(GridLength.Star));
        header.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        header.Add(titleLabel);
        header.Add(closeButton, 0, 1);

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                header,
                _webView,
                _status
            }
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(_webView, 1);
        Grid.SetRow(_status, 2);

        _webView.HandlerChanged += OnWebViewHandlerChanged;
        _webView.Navigating += OnWebViewNavigating;
        _webView.Navigated += OnWebViewNavigated;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the sign-in page modally. Returns true when a YouTube account
    /// was captured and saved.
    /// </summary>
    public static async Task<bool> RunLoginAsync(IServiceProvider services, Page parentPage)
    {
        var page = new YouTubeWebLoginPage(services);
        try
        {
            await parentPage.Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            Log($"cannot push page: {ex.Message}");
            return false;
        }

        return await page._tcs.Task;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  WebView setup
    // ─────────────────────────────────────────────────────────────────────────

    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        // Configure the NATIVE webview once, before the first navigation
        // paints. The URL itself is already assigned (constructor) — do NOT
        // assign Source here again, that would reload the page.
        if (_loaded) return;
        _loaded = true;

#if ANDROID
        if (_webView.Handler?.PlatformView is Android.Webkit.WebView native)
        {
            native.Settings.JavaScriptEnabled = true;
            native.Settings.DomStorageEnabled = true;

            var cm = Android.Webkit.CookieManager.Instance;
            if (cm != null)
            {
                cm.SetAcceptCookie(true);
                cm.SetAcceptThirdPartyCookies(native, true);
            }
        }
#endif
    }

    /// <summary>
    /// After a successful sign-in, YouTube sometimes redirects to a non-HTTP
    /// URL (intent://…#Intent;…;end to open the YouTube Music / Play Store
    /// app, market://, …). Rendering those in the WebView aborts the flow
    /// with ERR_UNKNOWN_URL_SCHEME before we can harvest cookies — the same
    /// bug Metrolist fixed in 2026 (commit 4d4b2a7). We intercept those
    /// navigations: intent:// links with a browser_fallback_url continue
    /// in the WebView; everything else is simply swallowed. The sign-in is
    /// usually already complete at that point, so we also retry the cookie
    /// harvest (Navigated does not re-fire for a canceled navigation).
    /// </summary>
    private void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        string url = e.Url ?? "";

        // Renderable schemes — let the WebView handle them.
        // about:/data:/blob:/javascript: are NATIVE WebView URIs: canceling
        // them calls StopLoading() and can abort an in-flight load — that is
        // exactly how v4 turned the sign-in page into a white screen (the
        // handler's own about:blank placeholder got canceled right after the
        // real navigation had started; see the file header). Never touch them.
        bool renderable =
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
        if (renderable || string.IsNullOrEmpty(url)) return;

        // Non-renderable schemes (intent://, market://, mailto:, …) — the
        // WebView would abort with ERR_UNKNOWN_URL_SCHEME.
        e.Cancel = true;
        Log($"intercepted non-http navigation: {url}");

        // intent://…;S.browser_fallback_url=<url>;…;end → follow the explicit
        // fallback URL and keep the sign-in flow inside the WebView.
        if (url.StartsWith("intent:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    url, @"browser_fallback_url=([^;&]+)");
                if (match.Success)
                {
                    string fallback = Uri.UnescapeDataString(match.Groups[1].Value);
                    if (fallback.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"intent redirect → browser_fallback_url: {fallback}");
                        _webView.Source = new UrlWebViewSource { Url = fallback };
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"intent redirect parse failed: {ex.Message}");
            }
        }

        // No fallback to follow: the sign-in has typically JUST finished on
        // the YouTube page we are still on — harvest cookies right now,
        // because Navigated will not re-fire for the canceled navigation.
        if (_lastHttpUrl.StartsWith("https://music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            _lastHttpUrl.StartsWith("https://www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            _ = TryCaptureAsync();
        }
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_captured) return;

        string url = e.Url;
        Log($"navigated: {url}");

        // Remember the last HTTP page — OnWebViewNavigating needs it to
        // retry the cookie harvest after swallowing a non-http redirect.
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _lastHttpUrl = url;

        // One-shot: log the user agent the page actually saw, so the next
        // logcat PROVES whether the default UA is live (no more guessing).
        if (!_uaLogged && url.StartsWith("https://accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            _uaLogged = true;
            try
            {
                string ua = await TryEvaluateJs("navigator.userAgent");
                Log($"live user-agent: {(string.IsNullOrWhiteSpace(ua) ? "(unavailable)" : ua)}");
            }
            catch { /* diagnostic only */ }
        }

        // Google renders the "This browser or app may not be secure"
        // block INLINE on accounts.google.com — there is no dedicated
        // error URL in the ServiceLogin flow. Sniff the page text
        // (cheap, once per navigation) so the user gets guidance
        // instead of a dead end.
        if (url.StartsWith("https://accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            bool blocked = url.Contains("disallowed_useragent", StringComparison.OrdinalIgnoreCase);
            if (!blocked)
            {
                string sniff = await TryEvaluateJs(
                    "(function(){var t=(document.body&&document.body.innerText)||'';" +
                    "return (t.indexOf('may not be secure')>=0||t.indexOf('not be secure')>=0)?'1':'0';})()");
                blocked = sniff == "1";
            }
            if (blocked)
            {
                Log("Google refused the embedded sign-in (browser/app not secure)");
                _status.Text =
                    "Google refuses to sign in for this account inside the embedded browser\n" +
                    "(Advanced Protection and Workspace accounts do this).\n" +
                    "Close this page and use \"Sign in with Google (device code)\" " +
                    "from the YouTube Accounts menu instead — it needs no browser.";
            }
            return;
        }

        if (!url.StartsWith("https://music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await TryCaptureAsync();
    }

    /// <summary>
    /// Harvest cookies from the platform cookie manager and save the account.
    /// Idempotent via the _captured flag; shared by OnWebViewNavigated (page
    /// navigations) and OnWebViewNavigating (swallowed post-sign-in
    /// redirects, where Navigated never re-fires).
    /// </summary>
    private async Task TryCaptureAsync()
    {
        if (_captured) return;

        try
        {
            string? cookie = await GetPlatformCookiesAsync();
            if (!HasRequiredAuthCookies(cookie))
            {
                _status.Text = "Page loaded but no session cookies yet — they appear once the sign-in completes.";
                Log("on a YouTube page but no session cookies yet — waiting for the sign-in to complete");
                return;
            }

            _captured = true;
            _status.Text = "Cookies captured — saving account…";
            bool saved = await SaveAccountAsync(cookie!);
            _status.Text = saved
                ? "Signed in. You can close this page."
                : "Cookies were captured but saving failed — see logs.";

            await Task.Delay(1200);
            await CloseAsync();
        }
        catch (Exception ex)
        {
            _captured = false;
            _status.Text = $"Capture failed: {ex.Message}";
            Log($"capture error: {ex}");
        }
    }

    private async Task CloseAsync()
    {
        _tcs.TrySetResult(_captured);
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            Log($"pop failed: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Cookie capture
    // ─────────────────────────────────────────────────────────────────────────

    private Task<string?> GetPlatformCookiesAsync()
    {
#if ANDROID
        var cm = Android.Webkit.CookieManager.Instance;
        if (cm == null) return Task.FromResult<string?>(null);
        try { cm.Flush(); } catch { /* best effort */ }

        // Music pages set cookies on .youtube.com; capture both hosts and merge.
        string music = cm.GetCookie("https://music.youtube.com/") ?? "";
        string www = cm.GetCookie("https://www.youtube.com/") ?? "";
        return Task.FromResult(MergeCookies(music, www));
#elif WINDOWS
        return GetPlatformCookiesWindowsAsync();
#else
        return Task.FromResult<string?>(null);
#endif
    }

#if WINDOWS
    private async Task<string?> GetPlatformCookiesWindowsAsync()
    {
        try
        {
            var wv2 = _webView.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.WebView2;
            var core = wv2?.CoreWebView2;
            if (core == null) return null;

            var map = new Dictionary<string, string>();
            foreach (string host in new[] { "https://music.youtube.com/", "https://www.youtube.com/" })
            {
                var cookies = await core.CookieManager.GetCookiesAsync(host);
                foreach (var c in cookies)
                    map.TryAdd(c.Name, c.Value);
            }
            return string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebLogin] Windows cookie capture failed: {ex.Message}");
            return null;
        }
    }
#endif

    internal static string? MergeCookies(params string?[] cookieStrings)
    {
        var map = new Dictionary<string, string>();
        foreach (string? cs in cookieStrings)
        {
            if (string.IsNullOrWhiteSpace(cs)) continue;
            foreach (string part in cs.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part[..eq].Trim();
                string value = part[(eq + 1)..].Trim();
                if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                    map[name] = value;
            }
        }
        return map.Count == 0 ? null : string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <summary>Cookie session is usable when it carries SAPISID and the PSID pair.</summary>
    internal static bool HasRequiredAuthCookies(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return false;

        bool hasSapisid = false;
        bool hasSession = false;
        foreach (string part in cookie.Split(';'))
        {
            string name = part.Trim();
            int eq = name.IndexOf('=');
            if (eq > 0) name = name[..eq];

            if (name.Equals("SAPISID", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase))
                hasSapisid = true;

            if (name.Equals("__Secure-1PSID", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SID", StringComparison.OrdinalIgnoreCase))
                hasSession = true;
        }
        return hasSapisid && hasSession;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Token/visitor-data harvest + persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Debug.WriteLine is compiled out of release builds; mirror the
    /// important traces through android Log (tag "Musicthingy") so they
    /// are visible with `adb logcat -s Musicthingy` in every build
    /// configuration.
    /// </summary>
    private static void Log(string message)
    {
        Debug.WriteLine($"[WebLogin] {message}");
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }

    private async Task<bool> SaveAccountAsync(string cookie)
    {
        // Self-healing: works with or without a DI registration
        // (zip-only installs have none — that made v1 silently do
        // nothing after a successful sign-in). EnsureInitializedAsync
        // is idempotent and its failure is non-fatal here.
        var accounts = AccountManager.Resolve(_services);
        try { await accounts.EnsureInitializedAsync(); }
        catch (Exception ex) { Log($"account store init error: {ex.Message}"); }

        // ── visitor data / data-sync id from the page context ───────────────
        string visitorData = await TryEvaluateJs(
            "(window.yt && window.yt.config_ && window.yt.config_.VISITOR_DATA) || " +
            "(window.ytcfg && window.ytcfg.get && window.ytcfg.get('VISITOR_DATA')) || ''");

        string dataSyncId = await TryEvaluateJs(
            "(window.ytcfg && window.ytcfg.get && window.ytcfg.get('DATASYNC_ID')) || ''");

        // ── account identity via innertube account_menu ─────────────────────
        var info = await YouTubeAccountInfoFetcher.FetchAsync(cookie, visitorData);
        string displayName =
            !string.IsNullOrWhiteSpace(info.Email) ? info.Email :
            !string.IsNullOrWhiteSpace(info.ChannelHandle) ? info.ChannelHandle :
            !string.IsNullOrWhiteSpace(info.Name) ? info.Name :
            $"YouTube ({DateTime.Now:yyyy-MM-dd HH:mm})";

        var data = new YouTubeAuthData
        {
            Cookie = cookie,
            VisitorData = string.IsNullOrWhiteSpace(visitorData) ? null : visitorData,
            DataSyncId = string.IsNullOrWhiteSpace(dataSyncId) ? null : dataSyncId,
            AccountName = info.Name,
            AccountEmail = info.Email,
            ChannelHandle = info.ChannelHandle
        };

        // Same email → update the existing account; otherwise add a new one
        // (multiple simultaneous accounts are supported; the new one becomes active).
        var existing = accounts.GetAccounts(MusicPlatform.YouTube)
            .FirstOrDefault(a => a.YouTubeKind == YouTubeAuthKind.Cookies &&
                                 !string.IsNullOrWhiteSpace(info.Email) &&
                                 a.DeserializeAuth<YouTubeAuthData>()?.AccountEmail == info.Email);

        if (existing != null)
        {
            existing.SerializeAuth(data);
            existing.DisplayName = displayName;
            await accounts.UpdateAccountAsync(existing);
            await accounts.SetActiveAsync(MusicPlatform.YouTube, existing.Id);
            Log($"updated account '{displayName}'");
        }
        else
        {
            var account = new AccountRecord
            {
                Platform = MusicPlatform.YouTube,
                YouTubeKind = YouTubeAuthKind.Cookies,
                DisplayName = displayName
            };
            account.SerializeAuth(data);
            await accounts.AddAccountAsync(account, makeActive: true);
            Log($"added account '{displayName}'");
        }

        // Mirror to the legacy preference keys so a rollback of this update
        // keeps working with the old resolver state.
        WriteLegacyMirror(data);

        return true;
    }

    private async Task<string> TryEvaluateJs(string script)
    {
        try
        {
            string? result = await _webView.EvaluateJavaScriptAsync(script);
            if (string.IsNullOrWhiteSpace(result) ||
                result == "null" || result == "undefined" || result == "''")
                return "";
            return result.Trim('"');
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebLogin] JS eval failed: {ex.Message}");
            return "";
        }
    }

    private static void WriteLegacyMirror(YouTubeAuthData data)
    {
        try
        {
            if (!string.IsNullOrEmpty(data.Cookie)) Preferences.Default.Set("InnerTubeCookie", data.Cookie);
            if (!string.IsNullOrEmpty(data.VisitorData)) Preferences.Default.Set("VisitorData", data.VisitorData);
            if (!string.IsNullOrEmpty(data.DataSyncId)) Preferences.Default.Set("DataSyncId", data.DataSyncId);
            if (!string.IsNullOrEmpty(data.AccountName)) Preferences.Default.Set("AccountName", data.AccountName);
            if (!string.IsNullOrEmpty(data.AccountEmail)) Preferences.Default.Set("AccountEmail", data.AccountEmail);
            if (!string.IsNullOrEmpty(data.ChannelHandle)) Preferences.Default.Set("AccountChannelHandle", data.ChannelHandle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebLogin] legacy mirror failed: {ex.Message}");
        }
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Resolves the signed-in account's display identity through the innertube
// account/account_menu endpoint (same call InnerTune's YouTube.accountInfo()
// uses). Non-fatal on failure — the caller falls back to a timestamp name.
// ────────────────────────────────────────────────────────────────────────────

public class YouTubeAccountInfo
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string ChannelHandle { get; set; } = "";
}

public static class YouTubeAccountInfoFetcher
{
    private const string AccountMenuUrl =
        "https://music.youtube.com/youtubei/v1/account/account_menu?prettyPrint=false";

    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task<YouTubeAccountInfo> FetchAsync(string cookie, string? visitorData)
    {
        var info = new YouTubeAccountInfo();
        try
        {
            string? sapisidHash = YouTubeResolverService.GenerateSapisidHash(cookie, "https://music.youtube.com");

            var payload = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB",
                        clientVersion = "2.20260819.00.00",
                        hl = "en",
                        gl = "US",
                        visitorData
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, AccountMenuUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("User-Agent", YouTubeWebLoginPage.WebViewUserAgent);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-Origin", "https://music.youtube.com");
            request.Headers.Add("Referer", "https://music.youtube.com/");
            if (!string.IsNullOrEmpty(sapisidHash))
                request.Headers.Authorization = new AuthenticationHeaderValue("SAPISIDHASH", sapisidHash.Split(' ')[1]);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[WebLogin] account_menu returned {(int)response.StatusCode}");
                return info;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            WalkForAccountInfo(doc.RootElement, info);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebLogin] account info fetch failed: {ex.Message}");
        }
        return info;
    }

    /// <summary>
    /// The account_menu payload shape changes between deployments, so walk it
    /// generically and pick up accountName / email / channelHandle simpleText
    /// fields wherever they appear.
    /// </summary>
    private static void WalkForAccountInfo(JsonElement element, YouTubeAccountInfo info)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("accountName") || prop.NameEquals("email") || prop.NameEquals("channelHandle"))
                    {
                        string? value = ExtractSimpleText(prop.Value);
                        if (!string.IsNullOrEmpty(value))
                        {
                            if (prop.NameEquals("accountName") && string.IsNullOrEmpty(info.Name)) info.Name = value;
                            else if (prop.NameEquals("email") && string.IsNullOrEmpty(info.Email)) info.Email = value;
                            else if (prop.NameEquals("channelHandle") && string.IsNullOrEmpty(info.ChannelHandle)) info.ChannelHandle = value;
                        }
                    }
                    WalkForAccountInfo(prop.Value, info);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkForAccountInfo(item, info);
                break;
        }
    }

    private static string? ExtractSimpleText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                return st.GetString();
            if (element.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
        }
        return null;
    }
}
