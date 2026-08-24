using CommunityToolkit.Maui.Views;
using Musicthingy.Services;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Action setting that opens a dialog where the user pastes the full OpenTune
/// auth block (all 7 fields at once). Parses it and populates all the
/// individual auth settings.
///
/// This is the easiest way to transfer credentials from OpenTune — just copy
/// the entire block from OpenTune's export and paste it here.
///
/// Expected format (matches OpenTune exactly):
///   ***INNERTUBE COOKIE*** =__Secure-3PSIDTS=...; SSID=...; SAPISID=...; ...
///   ***VISITOR DATA*** =CgTqZlN2ckRyUVdaWSiU...
///   ***DATASYNC ID*** =112954081425229850435
///   ***PO TOKEN*** =ChBhpP4v4KR3CO_RzcTtfyJe...
///   ***ACCOUNT NAME*** =user
///   ***ACCOUNT EMAIL*** =user@gmail.com
///   ***ACCOUNT CHANNEL HANDLE*** =@user
/// </summary>
public class ImportYouTubeAuthSetting : ActionSetting
{
    // Lazily resolved at call time to avoid circular dependency:
    //   SettingsManager -> IEnumerable<ISetting> -> ImportYouTubeAuthSetting -> SettingsManager
    private readonly IServiceProvider _services;

    public override string Key => "ImportYouTubeAuth";
    public override string Title => "Import YouTube Account";
    public override SettingCategory Category => SettingCategory.General;
    //public override string Icon => "log_in_icon.png";

    private SettingsManager? SettingsManager => _services.GetService<SettingsManager>();

    protected override string GetBaseDescription()
    {
        var sm = SettingsManager;
        if (sm == null)
            return "Paste OpenTune auth block to enable restricted-video playback.";

        // Use GetAllSettings (not GetVisibleSettings) because the individual
        // auth settings are in SettingCategory.Hidden.
        var cookie = sm.GetAllSettings()
            .OfType<InnerTubeCookieSetting>()
            .FirstOrDefault();

        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
            return "Paste OpenTune auth block to enable restricted-video playback.";

        var name = sm.GetAllSettings()
            .OfType<AccountNameSetting>()
            .FirstOrDefault()?.Value ?? "Unknown";

        return $"Linked: {name}. Tap to re-import.";
    }

    public ImportYouTubeAuthSetting(IServiceProvider services)
    {
        _services = services;
    }

    public override async void ExecuteAction()
    {
        try
        {
#if ANDROID || WINDOWS
            // Show a text-entry dialog. The user pastes the full OpenTune block.
            // We use a simple DisplayPromptAsync with multiline.
            var page = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (page == null) return;

            string? pasted = await page.DisplayPromptAsync(
                "Import YouTube Auth",
                "Paste the full block from OpenTune\n(all ***...*** lines):",
                "Import",
                "Cancel",
                placeholder: "***INNERTUBE COOKIE*** =...",
                maxLength: -1,
                keyboard: Keyboard.Default);

            if (string.IsNullOrWhiteSpace(pasted))
                return;

            var parsed = OpenTuneAuthParser.Parse(pasted);

            if (!parsed.HasAnyData)
            {
                await page.DisplayAlertAsync(
                    "Import Failed",
                    "Could not parse any auth fields.\nMake sure you copied the full block\nwith ***INNERTUBE COOKIE*** etc.",
                    "OK");
                return;
            }

            ApplyParsedValues(parsed);

            int fieldCount = parsed.CountSetFields();

            await page.DisplayAlertAsync(
                "Import Successful",
                $"Imported {fieldCount} field(s):\n" +
                (parsed.Cookie != null ? "✓ Cookie\n" : "") +
                (parsed.VisitorData != null ? "✓ Visitor Data\n" : "") +
                (parsed.DataSyncId != null ? "✓ DataSync ID\n" : "") +
                (parsed.PoToken != null ? "✓ PO Token\n" : "") +
                (parsed.AccountName != null ? "✓ Account Name\n" : "") +
                (parsed.AccountEmail != null ? "✓ Account Email\n" : "") +
                (parsed.ChannelHandle != null ? "✓ Channel Handle\n" : ""),
                "OK");
#else
            Debug.WriteLine("[ImportAuth] Not supported on this platform");
#endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImportAuth] Error: {ex.Message}");
        }
    }

    private void ApplyParsedValues(OpenTuneAuthParser.ParsedAuth parsed)
    {
        var sm = SettingsManager;
        if (sm == null) return;

        var settings = sm.GetAllSettings().ToList();

        if (parsed.Cookie != null)
            settings.OfType<InnerTubeCookieSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.Cookie);

        if (parsed.VisitorData != null)
            settings.OfType<VisitorDataSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.VisitorData);

        if (parsed.DataSyncId != null)
            settings.OfType<DataSyncIdSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.DataSyncId);

        if (parsed.PoToken != null)
            settings.OfType<PoTokenSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.PoToken);

        if (parsed.AccountName != null)
            settings.OfType<AccountNameSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.AccountName);

        if (parsed.AccountEmail != null)
            settings.OfType<AccountEmailSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.AccountEmail);

        if (parsed.ChannelHandle != null)
            settings.OfType<AccountChannelHandleSetting>().FirstOrDefault()?.Let(s => s.Value = parsed.ChannelHandle);

        // Refresh the description so the UI updates
        RefreshDescription();
    }

    /// <summary>
    /// Public method to refresh the Description property from outside this class.
    /// Needed because OnPropertyChanged is protected and can't be called cross-class.
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
