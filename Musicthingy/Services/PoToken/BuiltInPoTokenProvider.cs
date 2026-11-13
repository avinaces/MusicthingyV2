using System.Diagnostics;

namespace Musicthingy.Services.PoToken;

// ────────────────────────────────────────────────────────────────────────────
// BuiltInPoTokenProvider — the hub of the built-in, ON-DEVICE GVS PO token
// generator (v8.0).
//
// YouTube's GVS (googlevideo) stops serving SABR stream segments after ~60
// seconds of media without an attestation token (STREAM_PROTECTION_STATUS
// "attestation pending"; a non-zero start offset gets NOTHING at all), and
// every seek/rebuffer hits the same wall — so a GVS PO token is required for
// usable SABR playback, not just for playing past the first minute.
//
// This hub fronts the platform implementation (Android: a hidden WebView that
// runs the youtube.com web player's own BotGuard attestation flow — see
// Platforms/Android/WebViewPoTokenProvider.cs; the minted tokens are
// content-bound to the VIDEO ID, exactly what the web player attaches to its
// SABR/UMP requests). The platform layer registers its mint hook once at
// startup; Windows has no on-device generator (the resolver then falls back
// to an external bgutil provider URL or a manually imported token pair).
//
// Design notes:
//   - tokens are cached per content binding for 10 minutes: minting is cheap
//     once the BotGuard minter is warm (~200-500ms), but the playback resolve
//     and the background cache resolve of the same video should not mint twice;
//   - a full generation failure starts a 2-minute cooldown, so a broken
//     generator (e.g. YouTube changed the challenge format) never adds tens
//     of seconds to every resolve — the SABR continuation carries playback
//     across the ~60s quota windows in the meantime;
//   - user cancellation (track switch) never triggers the cooldown;
//   - NEVER throws (except cancellation) — a null return simply means "no
//     token, stream unattested".
// ────────────────────────────────────────────────────────────────────────────
public static class BuiltInPoTokenProvider
{
    /// <summary>
    /// Platform mint hook: (contentBinding, cancellationToken) => token.
    /// Registered once at startup by Platforms/Android/WebViewPoTokenProvider
    /// .Initialize(). Null on platforms without an on-device generator.
    /// </summary>
    public static Func<string, CancellationToken, Task<string?>>? MintImplementation { get; set; }

    /// <summary>
    /// Master switch — pushed by the "PO Token Generator (built-in)" setting
    /// (Settings/Definitions/PoTokenBuiltInSetting.cs). Default: on.
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>
    /// v8.3: the youtube.com VISITOR_DATA captured alongside the BotGuard
    /// minter (window.yt.config_.VISITOR_DATA in the WebView page). This is
    /// the exact identity the attestation environment saw at init, so
    /// WEB-family /player requests travel with it instead of a value adopted
    /// from a VISIONOS/TVHTML5 responseContext blob. Null until the first
    /// successful minter setup; cleared when the WebView is destroyed.
    /// </summary>
    public static string? WebVisitorData { get; set; }

    /// <summary>
    /// v8.4: the real native User-Agent of the Android WebView running
    /// BotGuard (captured in WebViewPoTokenProvider.CreateWebView — never
    /// spoofed). Used by MWEB so WebView, /player, and the SABR
    /// StreamerContext match 1:1: same UA, same Client Hints, same platform.
    /// Null on non-Android platforms.
    /// </summary>
    public static string? WebViewUserAgent { get; set; }

    /// <summary>
    /// True when the built-in generator can actually mint: the master switch
    /// is ON and a platform mint hook is registered (Android). The resolver
    /// consults this to decide whether the manually imported ***PO TOKEN***
    /// pair stays live or steps aside (v8.1: a stale hidden manual token must
    /// neither shadow the on-device mint nor ride along on /player requests).
    /// </summary>
    public static bool IsActive => IsEnabled && MintImplementation != null;

    /// <summary>
    /// Platform n-challenge solver hook: (playerUrl, nValue, cancellationToken) => solvedN.
    /// Registered once at startup by Platforms/Android/WebViewPoTokenProvider.Initialize().
    /// </summary>
    public static Func<string, string, CancellationToken, Task<string?>>? SolveNChallengeImplementation { get; set; }

    public static Task<string?> SolveNChallengeAsync(
        string playerUrl, string nValue, CancellationToken cancellationToken)
    {
        if (SolveNChallengeImplementation is { } impl &&
            !string.IsNullOrEmpty(playerUrl) &&
            !string.IsNullOrEmpty(nValue))
        {
            return impl(playerUrl, nValue, cancellationToken);
        }
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// v8.7: combined signature + n challenge solver hook:
    /// (playerUrl, sValue, nValue, cancellationToken) => (solvedSig, solvedN).
    /// sValue is the "s" throttling parameter from a signatureCipher format
    /// (sp says which query param carries the solved signature); nValue the
    /// "n" transform input. Either may be null — only present challenges are
    /// solved. Registered once at startup by
    /// Platforms/Android/WebViewPoTokenProvider.Initialize().
    /// </summary>
    public static Func<string, string?, string?, CancellationToken, Task<(string? Sig, string? N)>>?
        SolveChallengesImplementation { get; set; }

    public static async Task<(string? Sig, string? N)> SolveChallengesAsync(
        string playerUrl, string? sValue, string? nValue, CancellationToken cancellationToken)
    {
        if (SolveChallengesImplementation is { } impl &&
            !string.IsNullOrEmpty(playerUrl) &&
            (!string.IsNullOrEmpty(sValue) || !string.IsNullOrEmpty(nValue)))
        {
            return await impl(playerUrl, sValue, nValue, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(nValue))
        {
            string? n = await SolveNChallengeAsync(playerUrl, nValue, cancellationToken).ConfigureAwait(false);
            return (null, n);
        }

        return (null, null);
    }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(2);

    private static readonly object _sync = new();
    private static readonly Dictionary<string, (string Token, DateTimeOffset MintedAt)> _cache = new();
    private static DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Mints (or reuses) a content-bound GVS PO token for one video. The
    /// BotGuard minter and its integrity token stay warm in the platform
    /// WebView, so only the first call per run pays the full attestation cost.
    /// </summary>
    public static async Task<string?> MintContentBoundTokenAsync(
        string contentBinding, CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, Task<string?>>? impl = MintImplementation;
        if (!IsEnabled || impl is null || string.IsNullOrEmpty(contentBinding))
            return null;

        lock (_sync)
        {
            // A recently failed generation is skipped for a while (see docs).
            if (_cooldownUntil > DateTimeOffset.UtcNow)
                return null;
            if (_cache.TryGetValue(contentBinding, out (string Token, DateTimeOffset MintedAt) cached) &&
                DateTimeOffset.UtcNow - cached.MintedAt < CacheTtl)
                return cached.Token;
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                string? token = await impl(contentBinding, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token))
                {
                    lock (_sync)
                        _cache[contentBinding] = (token, DateTimeOffset.UtcNow);
                    Log($"[PoToken] on-device GVS PO token minted for {contentBinding} " +
                        $"(attempt {attempt}, {token.Length} chars)");
                    return token;
                }
                Log($"[PoToken] on-device generator returned no token (attempt {attempt})");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the caller switched tracks — no cooldown
            }
            catch (Exception ex)
            {
                Log($"[PoToken] on-device generator error (attempt {attempt}): {ex.Message}");
            }

            if (attempt == 1)
            {
                try { await Task.Delay(400, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            }
        }

        lock (_sync)
            _cooldownUntil = DateTimeOffset.UtcNow + FailureCooldown;
        Log("[PoToken] on-device generator failed twice — cooling down for 2 minutes " +
            "(SABR playback continues through the quota-window continuations)");
        return null;
    }

    /// <summary>
    /// Drops a cached token — called by the SABR session when the server
    /// REJECTS the attached one (STREAM_PROTECTION_STATUS 3), so the next
    /// session or continuation refresh mints a fresh token instead of
    /// re-attaching the rejected value.
    /// </summary>
    public static void Invalidate(string contentBinding)
    {
        if (string.IsNullOrEmpty(contentBinding)) return;
        lock (_sync)
            _cache.Remove(contentBinding);
        Log($"[PoToken] cached token for {contentBinding} invalidated (rejected by GVS)");
    }

    private static void Log(string message)
    {
        Debug.WriteLine(message);
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }
}
