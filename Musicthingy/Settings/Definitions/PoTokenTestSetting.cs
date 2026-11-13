using CommunityToolkit.Maui.Extensions;
using System.Diagnostics;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// "Test" row for the built-in PO token generator (v8.0): mints a throwaway
/// on-device GVS PO token (content binding "test" — minting is free, nothing
/// validates it until it is attached to a stream) and shows the outcome and
/// the elapsed time, so the generator can be verified without playing a
/// SABR-only track. On failure, the [PoToken] lines in logcat
/// (adb logcat -s Musicthingy) carry the per-phase reason.
/// </summary>
public class PoTokenTestSetting : ActionSetting
{
    public override string Key => "PoTokenGeneratorTest";
    public override string Title => "PO Token Generator — Test";
    public override SettingCategory Category => SettingCategory.General;
    public override string Icon => "shield_icon.png";

    protected override string GetBaseDescription()
    {
#if ANDROID
        return "Mints a throwaway on-device GVS PO token with the built-in generator and shows the result — verifies the hidden-WebView BotGuard flow without playing a SABR-only track.";
#else
        return "Android-only feature (the built-in generator is a hidden WebView). On Windows, test a PO Token Provider URL by playing a SABR-only track and watching the [PoToken]/[SABR] logcat lines.";
#endif
    }

    public override async void ExecuteAction()
    {
        try
        {
#if ANDROID
            var page = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (page == null)
            {
                Log("no page on the window — cannot show the test result");
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            string? token = await Services.PoToken.BuiltInPoTokenProvider
                .MintContentBoundTokenAsync("test", CancellationToken.None);
            sw.Stop();

            if (!string.IsNullOrEmpty(token))
            {
                string head = token[..Math.Min(32, token.Length)];
                await page.DisplayAlertAsync("PO Token Generator",
                    $"Minted an on-device GVS PO token in {sw.Elapsed.TotalSeconds:0.0}s.\n\n" +
                    $"{head}…\n\n" +
                    $"({token.Length} chars, websafe base64)\n\n" +
                    "The generator works — SABR-only tracks now stream full-length with seeking.",
                    "OK");
            }
            else
            {
                await page.DisplayAlertAsync("PO Token Generator",
                    $"No token after {sw.Elapsed.TotalSeconds:0.0}s.\n\n" +
                    "See the [PoToken] lines in logcat (adb logcat -s Musicthingy) for the " +
                    "per-phase reason (page fetch / challenge / BotGuard VM / GenerateIT).\n\n" +
                    "The generator cools down for 2 minutes after two failures — retry after that.",
                    "OK");
            }
#else
            Debug.WriteLine("[PoToken] the built-in generator is Android-only");
#endif
        }
        catch (Exception ex)
        {
            Log($"test failed: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[PoToken] {message}");
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }
}
