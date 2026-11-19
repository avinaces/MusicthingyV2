using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.Services;

/// <summary>
/// A published release that is newer than the installed build, as read from the
/// project's public GitHub releases feed.
/// </summary>
/// <param name="Version">Parsed tag version, used only for the comparison.</param>
/// <param name="Tag">Tag as published ("v0.1.2") - what the user is told.</param>
/// <param name="ReleaseUrl">Human release page (release notes + APK download).</param>
public sealed record UpdateInfo(Version Version, string Tag, Uri ReleaseUrl);

/// <summary>
/// Checks GitHub for a newer Musicthingy release.
/// </summary>
/// <remarks>
/// The check is a courtesy, not a feature: offline, rate-limited (60 anonymous
/// requests/hour per IP - a once-per-launch check never gets close), a tag that
/// is not a version, or a repository without releases all end as "no update".
/// Nothing here may block or fail startup.
/// </remarks>
public sealed class UpdateService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/avinaces/MusicthingyV2/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// Returns the newest release when it is newer than the installed build,
    /// otherwise null.
    /// </summary>
    public async Task<UpdateInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        string installed = AppInfo.Current.VersionString;
        Version? current = ParseVersion(installed);
        if (current == null)
        {
            Debug.WriteLine($"[Update] Installed version '{installed}' is not parseable - skipping check");
            return null;
        }

        UpdateInfo? latest = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (latest == null) return null;

        bool newer = CompareVersions(latest.Version, current) > 0;
        Debug.WriteLine($"[Update] Installed {current}, latest {latest.Tag} ({(newer ? "update available" : "up to date")})");
        return newer ? latest : null;
    }

    private static async Task<UpdateInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await Http.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 just means nothing has been published yet (or the repo is
                // private); anything else is rate limiting or a network hop.
                Debug.WriteLine($"[Update] GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement tagValue) ? tagValue.GetString() : null;
            string? page = root.TryGetProperty("html_url", out JsonElement pageValue) ? pageValue.GetString() : null;

            Version? version = ParseVersion(tag);
            if (version == null || string.IsNullOrWhiteSpace(tag) ||
                !Uri.TryCreate(page, UriKind.Absolute, out Uri? releaseUrl))
            {
                Debug.WriteLine($"[Update] Unusable release payload: tag={tag ?? "(none)"} url={page ?? "(none)"}");
                return null;
            }

            return new UpdateInfo(version, tag, releaseUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// "v1.2.3", "1.2.3-beta.1" and "1.2" all parse; anything else is null.
    /// A suffix is ignored on purpose - a pre-release of a version the user
    /// already has must not read as an update.
    /// </summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string core = text.Trim();
        if (core.StartsWith('v') || core.StartsWith('V'))
            core = core[1..];

        int suffix = core.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0)
            core = core[..suffix];

        return Version.TryParse(core, out Version? version) ? version : null;
    }

    /// <summary>
    /// Compares major.minor.build.revision with missing parts counting as zero,
    /// so a "v0.2" tag equals a "0.2.0" build. (<see cref="Version"/> itself
    /// treats an absent component as -1, which would make every short tag look
    /// newer than the same release.)
    /// </summary>
    private static int CompareVersions(Version left, Version right)
    {
        ReadOnlySpan<int> a = [left.Major, left.Minor, Math.Max(left.Build, 0), Math.Max(left.Revision, 0)];
        ReadOnlySpan<int> b = [right.Major, right.Minor, Math.Max(right.Build, 0), Math.Max(right.Revision, 0)];

        for (int i = 0; i < a.Length; i++)
        {
            int result = a[i].CompareTo(b[i]);
            if (result != 0) return result;
        }

        return 0;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };

        // GitHub rejects API requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Musicthingy-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }
}
