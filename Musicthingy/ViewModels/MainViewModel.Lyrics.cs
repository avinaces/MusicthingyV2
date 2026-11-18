using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] public partial bool IsFetchingLyrics { get; set; }
    [ObservableProperty] public partial bool HasNoLyrics { get; set; }
    [ObservableProperty] public partial bool HasTimedLyrics { get; set; }
    [ObservableProperty] public partial bool HasStaticLyrics { get; set; }

    public ObservableCollection<TimedLyric> ActiveTimedLyrics { get; } = new();

    // LRC timestamp: [m:ss], [m:ss.xx], [m:ss.xxx] - 1-3 digit minutes (hour
    // long tracks), 1-2 digit seconds, optional fraction. A line may carry
    // SEVERAL tags ("[00:12.00][01:15.00]Chorus"), so every match is parsed.
    private static readonly Regex TimestampRegex = new(
        @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled);

    // Metadata lines that disguise themselves as lyrics inside the first 20s:
    // "Lời: Name", "Nhạc: Name", "Lyrics by: Name", "作词： Name". The label
    // before the colon must be a KNOWN metadata word - a generic "first word
    // followed by a colon" pattern also matched real lyrics such as
    // "She said: stop", silently deleting them.
    private static readonly Regex MetadataLineRegex = new(
        @"^(?<key>[^:：]{1,30})[:：]",
        RegexOptions.Compiled);

    private static readonly string[] MetadataKeys =
    {
        "lyric", "lời", "nhạc", "作词", "作詞", "作曲", "歌词", "歌詞",
        "artist", "title", "track", "composer", "songwriter", "translation",
        "ترجمة", "romaniz", "romaji", "subtitle", "singer", "performed", "written",
    };

    // Timestamps this close belong to the same "line slot" - that is where
    // bilingual files put the original and its translation.
    private const double InterleavedTranslationWindowMs = 100;

    // Metadata often hides in the intro; only filter it there.
    private const double MetadataFilterSeconds = 20;

    // Cancel + version guard for Find Lyrics: a fetch belongs to the track it
    // was started for, and a newer request supersedes an older one.
    private CancellationTokenSource? _lyricsFetchCts;
    private int _lyricsFetchGeneration;

    private void ParseLyrics(string? rawLyrics)
    {
        ActiveTimedLyrics.Clear();
        HasNoLyrics = string.IsNullOrWhiteSpace(rawLyrics);
        HasTimedLyrics = false;
        HasStaticLyrics = false;

        if (HasNoLyrics || rawLyrics == null) return;

        var lines = rawLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        bool hasTimed = false;
        var lyricMap = new Dictionary<TimeSpan, string>();

        foreach (var line in lines)
        {
            var matches = TimestampRegex.Matches(line);
            if (matches.Count == 0)
                continue;

            // The text that follows the LAST tag on the line applies to every
            // tag on it - that is what makes compressed LRC lines
            // ("[00:12.00][01:15.00]Chorus") work.
            Match last = matches[matches.Count - 1];
            string text = line[(last.Index + last.Length)..].Trim();

            foreach (Match match in matches)
            {
                TimeSpan time = ParseTimestamp(match);

                // Filter out fake "metadata" disguising as lyrics in the
                // intro. Only lines that are actually dropped count as
                // "filtered" - hasTimed is set below, next to the real insert.
                if (time.TotalSeconds < MetadataFilterSeconds && IsMetadataLine(text))
                    continue;

                AddTimedLyric(lyricMap, time, text);

                if (!string.IsNullOrWhiteSpace(text))
                    hasTimed = true;
            }
        }

        if (hasTimed)
        {
            HasTimedLyrics = true;

            // Add them to the UI in chronological order
            foreach (var kvp in lyricMap.OrderBy(k => k.Key))
            {
                ActiveTimedLyrics.Add(new TimedLyric { Time = kvp.Key, Text = kvp.Value });
            }

            // Always anchor the start so auto-follow has a target immediately
            // after a track switch. Inserted ONCE, AFTER the loop - the old code
            // inserted it INSIDE the loop, stacking one empty 0s line PER lyric
            // (N empty lines = several screens of blank above the first real
            // line, and auto-follow centering invisible empty lines at the
            // intro, which looked like the highlight was desynced).
            ActiveTimedLyrics.Insert(0, new TimedLyric { Time = TimeSpan.Zero, Text = string.Empty });
        }
        else
        {
            HasStaticLyrics = true;
        }
    }

    /// <summary>
    /// True for an intro line of the form "Label: value" whose label is a known
    /// lyrics-metadata word (lyrics, lời, nhạc, 作词 ...).
    /// </summary>
    private static bool IsMetadataLine(string text)
    {
        var match = MetadataLineRegex.Match(text);
        if (!match.Success)
            return false;

        string key = match.Groups["key"].Value.ToLowerInvariant();
        foreach (string metadataKey in MetadataKeys)
        {
            if (key.Contains(metadataKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses one LRC tag ("[01:23.45]" / "[1:23]") into a <see cref="TimeSpan"/>.
    /// </summary>
    private static TimeSpan ParseTimestamp(Match match)
    {
        int minutes = int.Parse(match.Groups[1].Value);
        int seconds = int.Parse(match.Groups[2].Value);

        int milliseconds = 0;
        if (match.Groups[3].Success)
        {
            string fraction = match.Groups[3].Value;
            milliseconds = int.Parse(fraction);
            // ".5" = 500ms, ".45" = 450ms, ".456" = 456ms
            if (fraction.Length == 1) milliseconds *= 100;
            else if (fraction.Length == 2) milliseconds *= 10;
        }

        return new TimeSpan(0, 0, minutes, seconds, milliseconds);
    }

    /// <summary>
    /// Inserts one lyric line, collapsing interleaved translations.
    /// </summary>
    /// <remarks>
    /// Bilingual LRC files put the original line and its translation at (nearly)
    /// the same timestamp. Keeping the FIRST non-empty line is what preserves
    /// the original: the previous code always overwrote, so a translation
    /// landing 20ms later replaced the real lyric, and an empty
    /// end-of-phrase marker within 100ms could replace a real lyric with a
    /// blank line. A later non-empty line may still fill a blank placeholder.
    /// </remarks>
    private static void AddTimedLyric(Dictionary<TimeSpan, string> lyricMap, TimeSpan time, string text)
    {
        bool newIsBlank = string.IsNullOrWhiteSpace(text);
        string value = newIsBlank ? " " : text;

        foreach (var existingKey in lyricMap.Keys.ToList())
        {
            if (Math.Abs((existingKey - time).TotalMilliseconds) >= InterleavedTranslationWindowMs)
                continue;

            bool existingIsBlank = string.IsNullOrWhiteSpace(lyricMap[existingKey]);
            if (existingIsBlank && !newIsBlank)
            {
                // Blank placeholder first, real lyric second - keep the lyric.
                lyricMap.Remove(existingKey);
                lyricMap[time] = value;
            }

            // Otherwise the first real lyric wins; drop the duplicate.
            return;
        }

        lyricMap[time] = value;
    }

    private void SyncActiveLyric(TimeSpan position)
    {
        if (!HasTimedLyrics || ActiveTimedLyrics.Count == 0) return;

        TimedLyric? active = null;
        foreach (var lyric in ActiveTimedLyrics)
        {
            if (position.TotalMilliseconds >= (lyric.Time.TotalMilliseconds - 300))
                active = lyric;
            else
                break;
        }

        // Deactivate the old line BEFORE activating the new one. Activating
        // first meant listeners (the lyrics view scrolls on activation) briefly
        // saw TWO active lines when seeking backwards - and could be sent to
        // the stale one.
        foreach (var lyric in ActiveTimedLyrics)
        {
            if (lyric.IsActive && !ReferenceEquals(lyric, active))
                lyric.IsActive = false;
        }

        if (active != null && !active.IsActive)
            active.IsActive = true;
    }

    [RelayCommand]
    private async Task FindLyricsAsync()
    {
        // Capture the track the search belongs to - the user can change tracks
        // while the network call is in flight, and the result must not be
        // written onto whatever track happens to be current afterwards.
        PlaylistItem? targetTrack = CurrentTrack;
        if (targetTrack == null) return;

        // A local file has no URL but still has a title - LRCLIB works from
        // title + artist alone, so only a completely blank track is unusable.
        if (string.IsNullOrWhiteSpace(targetTrack.Url) && string.IsNullOrWhiteSpace(targetTrack.Title))
            return;

        // A newer request supersedes the one in flight (e.g. the user skipped
        // to another track and pressed Find Lyrics again).
        int generation = ++_lyricsFetchGeneration;
        _lyricsFetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lyricsFetchCts = cts;

        IsFetchingLyrics = true;
        try
        {
            string? newLyrics = await LyricsClient.FetchLyricsAsync(
                targetTrack.Url ?? string.Empty,
                targetTrack.Title ?? string.Empty,
                targetTrack.Artist ?? string.Empty,
                cancellationToken: cts.Token);

            if (generation != _lyricsFetchGeneration)
                return; // superseded while we were waiting - the new call owns the UI

            if (!string.IsNullOrEmpty(newLyrics))
            {
                // Store on the track the search was started for, whatever is
                // playing now - otherwise switching tracks mid-search persisted
                // the old track's lyrics onto the new one.
                targetTrack.Lyrics = newLyrics;
                RequestSaveLibrary();

                if (ReferenceEquals(CurrentTrack, targetTrack))
                {
                    ParseLyrics(newLyrics);
                    // The track may be paused mid-song: without this the
                    // highlight would stay blank until the next playback tick.
                    SyncActiveLyric(CurrentPosition);
                }
            }
            else if (ReferenceEquals(CurrentTrack, targetTrack))
            {
                await ShowConfirmAsync("Lyrics", "Could not find any lyrics for this track.", "OK", ModalBackgroundTapBehavior.Ignore);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or timed out - nothing to report.
        }
        catch (Exception ex)
        {
            if (generation == _lyricsFetchGeneration)
                await ShowConfirmAsync("Error", $"Failed to fetch lyrics: {ex.Message}", "OK", ModalBackgroundTapBehavior.Ignore);
        }
        finally
        {
            if (ReferenceEquals(_lyricsFetchCts, cts))
            {
                _lyricsFetchCts = null;
                IsFetchingLyrics = false;
            }

            // The call that created it owns it; a superseded call still gets
            // here after its token was cancelled.
            cts.Dispose();
        }
    }
}
