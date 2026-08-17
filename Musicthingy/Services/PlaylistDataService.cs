using Musicthingy.Models;
using System.Text.Json;

namespace Musicthingy.Services;

public class PlaylistDataService
{
    public async Task<Playlist?> ImportPlaylistFromJsonAsync(string jsonContent)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Reconstruct directly into the UI Model
            var playlist = JsonSerializer.Deserialize<Playlist>(jsonContent, options);

            if (playlist != null && playlist.Items != null)
            {
                // Re-hook the parent references
                foreach (var item in playlist.Items)
                {
                    item.Playlist = playlist;
                }
            }

            return playlist;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing JSON: {ex.Message}");
            return null;
        }
    }
}