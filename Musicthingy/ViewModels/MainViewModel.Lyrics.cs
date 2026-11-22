using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] public partial bool IsFetchingLyrics { get; set; }
    [ObservableProperty] public partial bool HasNoLyrics { get; set; }
    [ObservableProperty] public partial bool HasTimedLyrics { get; set; }
    [ObservableProperty] public partial bool HasStaticLyrics { get; set; }

    // Source for the static lyrics label. Normally the track's raw lyrics, but
    // a file that failed the timed sanity checks is shown here with its LRC
    // tags stripped instead of as "[00:12.00] line" noise.
    [ObservableProperty] public partial string? StaticLyricsText { get; set; }

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

    // LRC global shift tag: "[offset:+500]" means every line appears 500 ms
    // later, "[offset:-200]" 200 ms earlier. Ignoring it (the old behaviour)
    // left whole files half a second out of sync. A bare number is positive.
    private static readonly Regex OffsetTagRegex = new(
        @"(?im)^[ \t]*\[offset:[ \t]*(?<ms>[+-]?\d{1,6})[ \t]*\]",
        RegexOptions.Compiled);

    // A lyrics file that claims a timestamp past this is corrupt (or the tag
    // was misread) - a single stray line at 10h would otherwise be kept and
    // flag the whole file as "timed" while nothing ever highlights.
    private static readonly TimeSpan MaxLyricTimestamp = TimeSpan.FromHours(4);

    // LRC metadata tags ("[ar:]", "[ti:]", "[al:]", "[by:]" ...). Not lyrics,
    // and TimestampRegex ignores most of them - this catches the ones a file
    // might leave behind when it is shown as static text.
    private static readonly Regex LrcMetadataTagRegex = new(
        @"^\[(?:ar|al|ti|by|re|ve|length|offset|tool|id|la|au|sign|hash):[^\]]*\]$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fewer timed lines than this is not a sync track: it is one stray tag in
    // an otherwise plain text file ("[Chorus]"), which used to switch the
    // whole view to timed mode with a single line and no follow at all.
    private const int MinTimedLyricLines = 2;

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
        // ROUND 13: the collection is being rebuilt - the cached active index
        // belongs to the OLD list. Reset it so the first SyncActiveLyric after
        // the rebuild cannot mis-compare against a stale index (which would
        // skip the activation when the new list happens to light the same
        // index, or ship a wrong highlight).
        _activeLyricIndex = -1;
        HasNoLyrics = string.IsNullOrWhiteSpace(rawLyrics);
        HasTimedLyrics = false;
        HasStaticLyrics = false;
        // The static view binds this rather than CurrentTrack.Lyrics because a
        // file that fails the timed sanity checks is shown here WITHOUT its
        // "[mm:ss]" tags.
        StaticLyricsText = rawLyrics;

        if (HasNoLyrics || rawLyrics == null) return;

        var lines = rawLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // The file-wide shift applies to every timestamp in it.
        TimeSpan offset = TimeSpan.FromMilliseconds(ReadOffsetMilliseconds(rawLyrics));

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
                TimeSpan time = ParseTimestamp(match) + offset;

                // A negative offset can push the first lines before the start
                // of the audio; they belong at 0 or cannot be shown at all.
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                if (time > MaxLyricTimestamp)
                {
                    Debug.WriteLine($"[Lyrics] dropped timestamp past {MaxLyricTimestamp.TotalHours:F0}h: {time}");
                    continue;
                }

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

        // "hasTimed" only says SOME line carried text; a one-line file is not
        // a usable sync track. Count the real lines before switching modes.
        int timedLineCount = lyricMap.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value));
        if (timedLineCount < MinTimedLyricLines)
            hasTimed = false;

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
            // Only files that actually carried timestamps need their tags
            // stripped; plain lyrics keep their blank lines between stanzas.
            StaticLyricsText = lyricMap.Count > 0 ? BuildStaticText(rawLyrics) : rawLyrics;
        }
    }

    /// <summary>
    /// The "[offset:]" value in milliseconds, 0 when the file has none.
    /// </summary>
    private static double ReadOffsetMilliseconds(string rawLyrics)
    {
        var match = OffsetTagRegex.Match(rawLyrics);
        if (!match.Success)
            return 0;

        return int.TryParse(match.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms)
            ? ms
            : 0;
    }

    /// <summary>
    /// Plain text of a file that carried timestamps but is shown statically:
    /// the line text is kept, the tags and "[ar:...]"-style metadata are
    /// stripped, and a single blank line is preserved where the file had one
    /// (that is the stanza break).
    /// </summary>
    private static string BuildStaticText(string rawLyrics)
    {
        var builder = new StringBuilder();
        bool lastLineWasBlank = true; // suppress leading blank lines

        foreach (string rawLine in rawLyrics.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                if (!lastLineWasBlank)
                {
                    builder.AppendLine();
                    lastLineWasBlank = true;
                }
                continue;
            }

            if (LrcMetadataTagRegex.IsMatch(line))
                continue;

            var matches = TimestampRegex.Matches(line);
            if (matches.Count > 0)
            {
                Match last = matches[matches.Count - 1];
                line = line[(last.Index + last.Length)..].Trim();
                if (line.Length == 0)
                {
                    // A bare "[00:30.00]" is a break in the song, not a line.
                    if (!lastLineWasBlank)
                    {
                        builder.AppendLine();
                        lastLineWasBlank = true;
                    }
                    continue;
                }
            }

            builder.AppendLine(line);
            lastLineWasBlank = false;
        }

        return builder.Length > 0 ? builder.ToString().Trim() : rawLyrics;
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

    // ROUND 13: index of the currently active line (-1 = none). SyncActiveLyric
    // runs on EVERY 16Hz playback tick; the old code walked the WHOLE lyric
    // list twice per tick (finder loop + deactivation loop) - O(n) x 16Hz of
    // managed work on the UI thread even when nothing changed, and one of the
    // things a debug build cannot absorb (no AOT, interpreted bindings, no
    // layout caching). With the index cached the common case - same active
    // line as the last tick - is a single comparison.
    private int _activeLyricIndex = -1;

    private void SyncActiveLyric(TimeSpan position)
    {
        if (!HasTimedLyrics || ActiveTimedLyrics.Count == 0) return;

#if DEBUG
        // ROUND 13: debug perf HUD counter (see DebugPerfHud.cs) - one per
        // tick call (16Hz while playing).
        Views.DebugPerfHud.CountLyricScan();
#endif

        // Finder loop, kept EXACTLY as upstream: a line is active from 300ms
        // BEFORE its timestamp (the finder's pre-roll) until the next line's
        // pre-roll. Binary-search on the raw timestamps then adjust for the
        // 300ms pre-roll window, so the same line lights at the same instant
        // as before - identical behavior, O(log n) instead of O(n).
        var list = (IList<TimedLyric>)ActiveTimedLyrics;
        long posMs = (long)position.TotalMilliseconds;
        long target = posMs + 300; // first line with Time >= posMs-300ms
        int lo = 0, hi = list.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Time.TotalMilliseconds >= target) hi = mid - 1;
            else { idx = mid; lo = mid + 1; }
        }
        // idx = last line whose Time < posMs+300 == the last line the old
        // loop would have marked active.
        TimedLyric? active = idx >= 0 ? list[idx] : null;

        // Common case on a 16Hz tick: nothing changed since the last tick -
        // one field comparison, zero lyric writes, zero message sends.
        if (idx == _activeLyricIndex)
            return;
        _activeLyricIndex = idx;

#if DEBUG
        // ROUND 13: debug perf HUD counter - only ticks that actually moved
        // the active line (the rest are the guard's no-ops).
        Views.DebugPerfHud.CountLyricScanHit();
#endif

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

    /// <summary>
    /// Parses the current track's lyrics again. The view model only does that
    /// on a track change, so after the Edit Track sheet replaced the text the
    /// pane kept showing the old lines (or none) until another track started.
    /// </summary>
    public void RefreshLyricsView()
    {
        ParseLyrics(CurrentTrack?.Lyrics);

        // A track may be paused mid-song; without this the highlight stays
        // blank until the next playback tick.
        if (HasTimedLyrics)
            SyncActiveLyric(CurrentPosition);
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
                // ExternalArtist, NOT PlaylistItem.Artist: that property falls
                // back to the playlist name ("Favorites", "Workout"), which
                // was sent to LRCLIB as the artist, stopped LyricsClient from
                // reading the real author from YouTube metadata, and disabled
                // the "Artist - Song" title split.
                targetTrack.ExternalArtist ?? string.Empty,
                duration: targetTrack.Duration > TimeSpan.Zero ? targetTrack.Duration : null,
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
