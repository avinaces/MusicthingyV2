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

    private void ParseLyrics(string? rawLyrics)
    {
        ActiveTimedLyrics.Clear();
        HasNoLyrics = string.IsNullOrWhiteSpace(rawLyrics);
        HasTimedLyrics = false;
        HasStaticLyrics = false;

        if (HasNoLyrics || rawLyrics == null) return;

        var lines = rawLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]\s*(.*)");

        bool hasTimed = false;
        var lyricMap = new Dictionary<TimeSpan, string>();

        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                hasTimed = true;
                int m = int.Parse(match.Groups[1].Value);
                int s = int.Parse(match.Groups[2].Value);
                int ms = int.Parse(match.Groups[3].Value);
                if (match.Groups[3].Value.Length == 2) ms *= 10;

                string text = match.Groups[4].Value.Trim();
                TimeSpan time = new TimeSpan(0, 0, m, s, ms);

                // 1. Filter out fake "metadata" disguising as lyrics in the first 20 seconds
                // Matches lines like: "Lời: Name", "Nhạc: Name", "Lyrics: Name", "作词： Name"
                if (time.TotalSeconds < 20 && Regex.IsMatch(text, @"^([\p{L}]+\s*){1,3}[:：].*"))
                {
                    continue;
                }

                // 2. Filter out interleaved translations
                // Check if a lyric already exists within 100ms of this timestamp.
                bool foundExisting = false;
                TimeSpan existingKey = TimeSpan.Zero;

                foreach (var k in lyricMap.Keys)
                {
                    if (Math.Abs((k - time).TotalMilliseconds) < 100)
                    {
                        foundExisting = true;
                        existingKey = k;
                        break;
                    }
                }

                if (foundExisting)
                {
                    lyricMap.Remove(existingKey);
                }

                // Insert the new line. By overwriting close/duplicate timestamps, 
                // we strip away the bilingual spam and keep a single clean line!
                lyricMap[time] = string.IsNullOrEmpty(text) ? " " : text;
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
        }
        else
        {
            HasStaticLyrics = true;
        }
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

        foreach (var lyric in ActiveTimedLyrics)
        {
            bool shouldBeActive = (lyric == active);
            if (lyric.IsActive != shouldBeActive)
            {
                lyric.IsActive = shouldBeActive;
            }
        }
    }

    [RelayCommand]
    private async Task FindLyricsAsync()
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.Url)) return;

        IsFetchingLyrics = true;
        try
        {
            string? newLyrics = await LyricsClient.FetchLyricsAsync(CurrentTrack.Url, CurrentTrack.Title ?? "", "");
            if (!string.IsNullOrEmpty(newLyrics))
            {
                CurrentTrack.Lyrics = newLyrics;
                ParseLyrics(newLyrics);
                RequestSaveLibrary();
            }
            else
            {
                await ShowConfirmAsync("Lyrics", "Could not find any lyrics for this track.", "OK", ModalBackgroundTapBehavior.Ignore);
            }
        }
        catch (Exception ex)
        {
            await ShowConfirmAsync("Error", $"Failed to fetch lyrics: {ex.Message}", "OK", ModalBackgroundTapBehavior.Ignore);
        }
        finally
        {
            IsFetchingLyrics = false;
        }
    }
}