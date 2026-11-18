using Musicthingy.Models;
using System.Diagnostics;

namespace Musicthingy.Search;

/// <summary>
/// Keeps a normalised, immutable snapshot of the library so typing never has
/// to walk the live ObservableCollections on the UI thread. MainViewModel calls
/// <see cref="Update"/> whenever the library or the open playlist changes.
/// </summary>
public sealed class LibrarySearchIndex
{
    private LibrarySearchSnapshot _snapshot = LibrarySearchSnapshot.Empty;
    private int _version;

    public LibrarySearchSnapshot Current => Volatile.Read(ref _snapshot);

    public void Update(IEnumerable<Playlist> playlists, Playlist? selectedPlaylist)
    {
        try
        {
            List<PlaylistSearchEntry> playlistEntries = new();
            List<TrackSearchEntry> trackEntries = new();

            foreach (Playlist playlist in playlists)
            {
                if (playlist == null) continue;

                playlistEntries.Add(new PlaylistSearchEntry(
                    playlist,
                    SearchText.Normalize(playlist.Name),
                    NormalizeTags(playlist.Tags),
                    $"{playlist.ItemCount} tracks · {playlist.DisplayDuration}"));

                foreach (PlaylistItem track in playlist.Items)
                {
                    if (track == null) continue;

                    trackEntries.Add(new TrackSearchEntry(
                        track,
                        playlist,
                        SearchText.Normalize(track.Title),
                        NormalizeTags(track.Tags),
                        SearchText.Normalize(playlist.Name),
                        $"{playlist.Name} · {track.DisplayDuration}",
                        playlist.IsSystemPlaylist));
                }
            }

            LibrarySearchSnapshot next = new(
                playlistEntries,
                trackEntries,
                selectedPlaylist,
                Interlocked.Increment(ref _version));

            Volatile.Write(ref _snapshot, next);
        }
        catch (Exception ex)
        {
            // A partially built index is worse than the previous one.
            Debug.WriteLine($"[Search] Index update failed: {ex.Message}");
        }
    }

    private static string NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags == null) return string.Empty;

        List<string> normalized = new();
        foreach (string tag in tags)
        {
            string value = SearchText.Normalize(tag);
            if (value.Length > 0) normalized.Add(value);
        }

        return string.Join(' ', normalized);
    }
}
