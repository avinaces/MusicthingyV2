using CommunityToolkit.Maui.Views;
using Musicthingy.Services;
using Musicthingy.Services.Accounts;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube account manager (multi-account). Replaces the old single-account
/// cookie paste with:
///   • "Sign in with Google (device code)" — OAuth device flow, no cookies
///     needed; the app gets auto-refreshing access tokens
///   • "Import cookies / OpenTune block" — legacy path, still supported
///   • multiple simultaneous accounts, one active per platform
///
/// The active account is pushed into YouTubeResolverService by the
/// AccountManager (OAuth → Bearer token provider, cookies → SAPISIDHASH).
/// </summary>
public class ImportYouTubeAuthSetting : ActionSetting
{
    // Resolved lazily at call time to avoid circular dependency:
    //   SettingsManager -> IEnumerable<ISetting> -> ImportYouTubeAuthSetting
    private readonly IServiceProvider _services;

    public override string Key => "ImportYouTubeAuth";
    public override string Title => "YouTube Accounts";
    public override SettingCategory Category => SettingCategory.General;
    // App icon convention: "<lucide-name>_icon.png" (lucide "log-in").
    public override string Icon => "log_in_icon.png";

    // AccountManager.Resolve self-initializes a shared account store when
    // this project has no DI registration for it (zip-only installs), so
    // the menu always works with zero MauiProgram changes.
    private AccountManager Accounts => AccountManager.Resolve(_services);

    protected override string GetBaseDescription()
    {
        var accounts = Accounts;
        var active = accounts.GetActive(MusicPlatform.YouTube);
        if (active == null)
            return "No account. Sign in with Google (browser or device code) or import cookies to enable restricted-video playback.";

        string kind = active.YouTubeKind == YouTubeAuthKind.OAuth ? "OAuth" : "cookies";
        int total = accounts.Count(MusicPlatform.YouTube);
        return total > 1
            ? $"Active: {active.DisplayName} ({kind}). {total} accounts linked."
            : $"Linked: {active.DisplayName} ({kind}).";
    }

    public ImportYouTubeAuthSetting(IServiceProvider services)
    {
        _services = services;

        // Bring the account store up at app start (a no-op when the
        // project already registers + initializes AccountManager in
        // DI). This migrates previously pasted cookies into the
        // multi-account store and pushes resolver state before the
        // first track resolves. It also subscribes the PSIDTS
        // session keep-alive (see AccountManager).
        _ = InitializeAndRefreshAsync();

        // Keep the visible description in sync with account changes.
        Accounts.AccountsChanged += () => RefreshDescription();
    }

    private async Task InitializeAndRefreshAsync()
    {
        try { await Accounts.EnsureInitializedAsync(); }
        catch (Exception ex) { Log($"account store init error: {ex.Message}"); }
        RefreshDescription();
    }

    public override async void ExecuteAction()
    {
        try
        {
#if ANDROID || WINDOWS
            var page = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (page == null)
            {
                Log("no page on the window — cannot show the account menu");
                return;
            }

            var accounts = Accounts;
            try { await accounts.EnsureInitializedAsync(); }
            catch (Exception ex) { Log($"account store init error: {ex.Message}"); }

            Log("opening account menu");

            // ── Build the menu: existing accounts + actions ─────────────────
            var ytAccounts = accounts.GetAccounts(MusicPlatform.YouTube).ToList();
            var activeId = accounts.GetActive(MusicPlatform.YouTube)?.Id;

            var choices = ytAccounts
                .Select(a => (a.Id == activeId ? "● " : "○ ") + a.DisplayName)
                .ToList();

            const string addWeb = "➕  Sign in with Google (in-app browser)";
            const string addOAuth = "➕  Sign in with Google (device code)";
            const string addCookies = "➕  Import cookies / OpenTune block";
            const string removeAccount = "🗑  Remove an account";

            var all = new List<string>(choices) { addWeb, addOAuth, addCookies, removeAccount };

            string choice = await page.DisplayActionSheetAsync(
                "YouTube Accounts", "Cancel", null, all.ToArray());

            if (choice == null || choice == "Cancel") return;

            if (choice == addWeb) { await RunWebLoginAsync(page); return; }
            if (choice == addOAuth) { await RunGoogleDeviceFlowAsync(page, accounts); return; }
            if (choice == addCookies) { await RunCookieImportAsync(page, accounts); return; }
            if (choice == removeAccount) { await RemoveAccountFlowAsync(page, accounts); return; }

            // An existing account was tapped → make it active.
            int index = choices.IndexOf(choice);
            if (index >= 0 && index < ytAccounts.Count)
            {
                await accounts.SetActiveAsync(MusicPlatform.YouTube, ytAccounts[index].Id);
                RefreshDescription();
                await page.DisplayAlertAsync("Account Switched",
                    $"Now using: {ytAccounts[index].DisplayName}", "OK");
            }
#else
            Debug.WriteLine("[YouTubeAccounts] Not supported on this platform");
#endif
        }
        catch (Exception ex)
        {
            Log($"error: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Google sign-in inside an embedded WebView (cookie capture)
    // ─────────────────────────────────────────────────────────────────────

    private async Task RunWebLoginAsync(Page page)
    {
        try
        {
            // The page itself resolves the AccountManager via _services, captures
            // every cookie/token and stores the account — see YouTubeWebLoginPage.
            bool signedIn = await Views.YouTubeWebLoginPage.RunLoginAsync(_services, page);
            if (signedIn)
                RefreshDescription();
        }
        catch (Exception ex)
        {
            Log($"web login error: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Google OAuth device flow (no cookies)
    // ─────────────────────────────────────────────────────────────────────

    private async Task RunGoogleDeviceFlowAsync(Page page, AccountManager accounts)
    {
        try
        {
            // Client id/secret: stored in Settings, prompted here if missing.
            string clientId = Preferences.Default.Get(AccountManager.OAuthClientIdPrefsKey, "");
            string clientSecret = Preferences.Default.Get(AccountManager.OAuthClientSecretPrefsKey, "");

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                await page.DisplayAlertAsync(
                    "One-time setup needed",
                    "YouTube sign-in needs a Google Cloud OAuth client of type\n" +
                    "'TVs and Limited Input devices'.\n\n" +
                    "1. console.cloud.google.com → create project\n" +
                    "2. Enable 'YouTube Data API v3'\n" +
                    "3. OAuth consent screen (External, add yourself as test user)\n" +
                    "4. Credentials → Create OAuth client ID → TV\n" +
                    "5. Enter the client ID & secret in Settings first",
                    "OK");
                return;
            }

            var code = await Musicthingy.Services.YouTube.GoogleDeviceFlowClient.RequestDeviceCodeAsync(clientId);

            // Kick the polling off BEFORE the dialog so approval can happen
            // while the dialog is open.
            var pollTask = Musicthingy.Services.YouTube.GoogleDeviceFlowClient.PollForTokenAsync(
                clientId, clientSecret, code);

            await page.DisplayAlertAsync(
                "Sign in with Google",
                $"On any device, open:\n\n{code.VerificationUrlWithCode}\n\n" +
                $"and approve access for code\n\n{code.UserCode}\n\n" +
                "Press OK once you've approved.",
                "OK");

            var token = await pollTask;

            var account = new AccountRecord
            {
                Platform = MusicPlatform.YouTube,
                YouTubeKind = YouTubeAuthKind.OAuth,
                DisplayName = $"Google account ({DateTime.Now:yyyy-MM-dd HH:mm})"
            };
            account.SerializeAuth(new YouTubeAuthData
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                AccessTokenExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn,
                Scope = token.Scope
            });

            await accounts.AddAccountAsync(account, makeActive: true);
            RefreshDescription();

            await page.DisplayAlertAsync("Signed In",
                "YouTube account added and activated.\n" +
                "Tokens refresh automatically — no cookie re-imports needed.", "OK");
        }
        catch (Musicthingy.Services.YouTube.GoogleDeviceFlowClient.DeviceFlowException ex)
        {
            await page.DisplayAlertAsync("Sign-in Failed", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            Log($"OAuth error: {ex.Message}");
            await page.DisplayAlertAsync("Sign-in Failed", ex.Message, "OK");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Legacy cookie / OpenTune import
    // ─────────────────────────────────────────────────────────────────────

    private async Task RunCookieImportAsync(Page page, AccountManager accounts)
    {
        string? pasted = await page.DisplayPromptAsync(
            "Import Cookies",
            "Paste the full OpenTune block\n(all ***...*** lines), or a raw cookie string:",
            "Import",
            "Cancel",
            placeholder: "***INNERTUBE COOKIE*** =...",
            maxLength: -1,
            keyboard: Keyboard.Default);

        if (string.IsNullOrWhiteSpace(pasted)) return;

        var parsed = OpenTuneAuthParser.Parse(pasted);

        // Also accept a bare cookie string (contains '=' and ';' but no *** markers).
        if (!parsed.HasAnyData && pasted.Contains('=') && pasted.Contains(';'))
            parsed.Cookie = pasted.Trim();

        if (!parsed.HasAnyData)
        {
            await page.DisplayAlertAsync(
                "Import Failed",
                "Could not parse any auth fields.\nMake sure you copied the full block\nwith ***INNERTUBE COOKIE*** etc.",
                "OK");
            return;
        }

        // Reuse an existing cookie account if there is one; otherwise create.
        var account = accounts.GetAccounts(MusicPlatform.YouTube)
            .FirstOrDefault(a => a.YouTubeKind == YouTubeAuthKind.Cookies);

        bool isNew = account == null;
        account ??= new AccountRecord
        {
            Platform = MusicPlatform.YouTube,
            YouTubeKind = YouTubeAuthKind.Cookies
        };

        var data = account.DeserializeAuth<YouTubeAuthData>() ?? new YouTubeAuthData();
        if (parsed.Cookie != null) data.Cookie = parsed.Cookie;
        if (parsed.VisitorData != null) data.VisitorData = parsed.VisitorData;
        if (parsed.DataSyncId != null) data.DataSyncId = parsed.DataSyncId;
        if (parsed.PoToken != null) data.PoToken = parsed.PoToken;
        if (parsed.AccountName != null) data.AccountName = parsed.AccountName;
        if (parsed.AccountEmail != null) data.AccountEmail = parsed.AccountEmail;
        if (parsed.ChannelHandle != null) data.ChannelHandle = parsed.ChannelHandle;

        account.DisplayName = !string.IsNullOrWhiteSpace(data.AccountEmail) ? data.AccountEmail
                           : !string.IsNullOrWhiteSpace(data.ChannelHandle) ? data.ChannelHandle
                           : !string.IsNullOrWhiteSpace(data.AccountName) ? data.AccountName
                           : "YouTube (cookies)";
        account.SerializeAuth(data);

        // Mirror to the legacy hidden settings so a rollback keeps working.
        WriteLegacyFromData(data);

        if (isNew)
            await accounts.AddAccountAsync(account, makeActive: true);
        else
            await accounts.UpdateAccountAsync(account);

        RefreshDescription();

        await page.DisplayAlertAsync("Import Successful",
            $"Imported {parsed.CountSetFields()} field(s) for '{account.DisplayName}'.", "OK");
    }

    private static void WriteLegacyFromData(YouTubeAuthData data)
    {
        if (!string.IsNullOrEmpty(data.Cookie)) Preferences.Default.Set("InnerTubeCookie", data.Cookie);
        if (!string.IsNullOrEmpty(data.VisitorData)) Preferences.Default.Set("VisitorData", data.VisitorData);
        if (!string.IsNullOrEmpty(data.DataSyncId)) Preferences.Default.Set("DataSyncId", data.DataSyncId);
        if (!string.IsNullOrEmpty(data.PoToken)) Preferences.Default.Set("PoToken", data.PoToken);
        if (!string.IsNullOrEmpty(data.AccountName)) Preferences.Default.Set("AccountName", data.AccountName);
        if (!string.IsNullOrEmpty(data.AccountEmail)) Preferences.Default.Set("AccountEmail", data.AccountEmail);
        if (!string.IsNullOrEmpty(data.ChannelHandle)) Preferences.Default.Set("AccountChannelHandle", data.ChannelHandle);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Remove account
    // ─────────────────────────────────────────────────────────────────────

    private async Task RemoveAccountFlowAsync(Page page, AccountManager accounts)
    {
        var ytAccounts = accounts.GetAccounts(MusicPlatform.YouTube).ToList();
        if (ytAccounts.Count == 0)
        {
            await page.DisplayAlertAsync("No Accounts", "There is no YouTube account to remove.", "OK");
            return;
        }

        string choice = await page.DisplayActionSheetAsync(
            "Remove YouTube Account", "Cancel", null,
            ytAccounts.Select(a => a.DisplayName).ToArray());

        if (choice == null || choice == "Cancel") return;

        var account = ytAccounts.FirstOrDefault(a => a.DisplayName == choice);
        if (account == null) return;

        bool confirm = await page.DisplayAlertAsync(
            "Remove Account",
            $"Remove '{account.DisplayName}'?\n\nIf this is a cookie account you may also want to clear the legacy fields with 'Clear YouTube Account'.",
            "Remove", "Cancel");
        if (!confirm) return;

        await accounts.RemoveAccountAsync(account);
        RefreshDescription();
    }

    /// <summary>
    /// Debug.WriteLine is compiled out of release builds; mirror the
    /// important traces through android Log (tag "Musicthingy") so they
    /// are visible with `adb logcat -s Musicthingy` in every build
    /// configuration.
    /// </summary>
    private static void Log(string message)
    {
        Debug.WriteLine($"[YouTubeAccounts] {message}");
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }

    /// <summary>
    /// Public method to refresh the Description property from outside this class.
    /// </summary>
    public void RefreshDescription()
    {
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Description)));
    }
}

/// <summary>
/// Extension: let-style null-safe invocation for settings.
/// </summary>
internal static class ObjectExtensions
{
    public static void Let<T>(this T? obj, Action<T> block) where T : class
    {
        if (obj != null) block(obj);
    }
}

/// <summary>
/// Parser for the OpenTune auth block format.
/// Handles the ***FIELD*** =value layout used by OpenTune's export/import.
/// </summary>
internal static class OpenTuneAuthParser
{
    public class ParsedAuth
    {
        public string? Cookie { get; set; }
        public string? VisitorData { get; set; }
        public string? DataSyncId { get; set; }
        public string? PoToken { get; set; }
        public string? AccountName { get; set; }
        public string? AccountEmail { get; set; }
        public string? ChannelHandle { get; set; }

        public bool HasAnyData =>
            Cookie != null || VisitorData != null || DataSyncId != null ||
            PoToken != null || AccountName != null || AccountEmail != null ||
            ChannelHandle != null;

        public int CountSetFields()
        {
            int count = 0;
            if (Cookie != null) count++;
            if (VisitorData != null) count++;
            if (DataSyncId != null) count++;
            if (PoToken != null) count++;
            if (AccountName != null) count++;
            if (AccountEmail != null) count++;
            if (ChannelHandle != null) count++;
            return count;
        }
    }

    /// <summary>
    /// Parses the full OpenTune auth block. Handles:
    /// - ***FIELD*** =value (with optional spaces around =)
    /// - ***FIELD***=value (no spaces)
    /// - ***FIELD*** = value (spaces around =)
    /// - Multi-line cookie values (joined into one line)
    /// - Lines without *** markers are ignored
    /// </summary>
    public static ParsedAuth Parse(string input)
    {
        var result = new ParsedAuth();

        if (string.IsNullOrWhiteSpace(input))
            return result;

        // Split into lines, but cookie values may span multiple lines.
        // We join lines until we hit the next *** marker.
        var lines = input.Split('\n', '\r');

        string? currentField = null;
        var currentValue = new System.Text.StringBuilder();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            // Check if this line starts a new ***FIELD*** block
            var match = Regex.Match(line, @"^\*{3}([^*]+)\*{3}\s*=\s*(.*)$");

            if (match.Success)
            {
                // Save the previous field if any
                if (currentField != null)
                    AssignField(result, currentField, currentValue.ToString().Trim());

                // Start new field
                currentField = match.Groups[1].Value.Trim().ToUpperInvariant();
                currentValue.Clear();
                currentValue.Append(match.Groups[2].Value);
            }
            else if (currentField != null)
            {
                // Continuation of the current field (multi-line cookie value)
                if (!string.IsNullOrWhiteSpace(line))
                {
                    currentValue.Append(' ');
                    currentValue.Append(line);
                }
            }
        }

        // Save the last field
        if (currentField != null)
            AssignField(result, currentField, currentValue.ToString().Trim());

        return result;
    }

    private static void AssignField(ParsedAuth result, string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        switch (fieldName)
        {
            case "INNERTUBE COOKIE":
            case "INNER TUBE COOKIE":
            case "COOKIE":
                result.Cookie = value;
                break;
            case "VISITOR DATA":
            case "VISITORDATA":
                result.VisitorData = value;
                break;
            case "DATASYNC ID":
            case "DATA SYNC ID":
            case "DATASYNCID":
                result.DataSyncId = value;
                break;
            case "PO TOKEN":
            case "POTOKEN":
                result.PoToken = value;
                break;
            case "ACCOUNT NAME":
            case "ACCOUNTNAME":
                result.AccountName = value;
                break;
            case "ACCOUNT EMAIL":
            case "ACCOUNTEMAIL":
                result.AccountEmail = value;
                break;
            case "ACCOUNT CHANNEL HANDLE":
            case "CHANNEL HANDLE":
            case "CHANNELHANDLE":
                result.ChannelHandle = value;
                break;
        }
    }
}
