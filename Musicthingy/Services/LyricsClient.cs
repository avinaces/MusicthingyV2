using System.Text.Json;

namespace Musicthingy.Services;

public static class LyricsClient
{
    // Reusable HTTP Client to prevent socket exhaustion
    public static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    static LyricsClient()
    {
        SharedClient.DefaultRequestHeaders.Add("User-Agent", "Musicthingy/1.5 (https://github.com/Musicthingy)");
    }

    /// <summary>
    /// Performs a manual LRCLIB search based on an exact query and preferred format.
    /// </summary>
    public static async Task<string?> SearchManualAsync(string query, bool wantTimed)
    {
        try
        {
            string url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            var response = await SharedClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var result in doc.RootElement.EnumerateArray())
                    {
                        string propName = wantTimed ? "syncedLyrics" : "plainLyrics";

                        if (result.TryGetProperty(propName, out var lyricObj) && lyricObj.ValueKind == JsonValueKind.String)
                        {
                            string? lrc = lyricObj.GetString();
                            if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsClient] Search Error: {ex.Message}");
            throw; // Re-throw to let the ViewModel handle the UI alert
        }

        return null;
    }
}