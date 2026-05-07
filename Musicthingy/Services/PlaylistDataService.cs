using Musicthingy.Models;
using System.Text.Json;

namespace Musicthingy.Services;

public class PlaylistDataService
{
    public async Task<Playlist?> ImportPlaylistFromJsonAsync(string jsonContent)
    {
        try
        {
            // 1. Read into the safe DTO

            PlaylistDto? dto = JsonSerializer.Deserialize<PlaylistDto>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return null;

            // 2. Map to UI Model

            var playlist = new Playlist
            {
                Name = dto.name,
                IconUrl = dto.icon,
                IconType = ParseIconType(dto.iconType),
                Source = dto.source,
                CreatedAt = dto.createdAt ?? DateTime.Now,
                UpdatedAt = dto.updatedAt ?? DateTime.Now
            };

            static PlaylistIconType ParseIconType(string? raw)
            {
                if (string.IsNullOrEmpty(raw)) return PlaylistIconType.Playlist;
                raw = raw.Trim();
                if (raw.Equals("playlist", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Playlist;
                if (raw.Equals("track", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.Track;
                if (raw.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return PlaylistIconType.YouTube;
                if (Enum.TryParse<PlaylistIconType>(raw, true, out var parsed)) return parsed;
                return PlaylistIconType.Playlist;
            }

            foreach (var playlistTag in dto.tags)
            {
                playlist.Tags.Add(playlistTag);
            }


            foreach (var itemDto in dto.items)
            {
                var item = new PlaylistItem
                {
                    Title = string.IsNullOrEmpty(itemDto.title) ? "Unknown Title" : itemDto.title,
                    Url = itemDto.url,
                    StartTime = itemDto.startTime,
                    EndTime = itemDto.endTime,
                    IconUrl = itemDto.icon,
                    Lyrics = itemDto.lyrics,
                    LastPlayedAt = itemDto.lastPlayedAt
                };

                if (itemDto.tags != null)
                {
                    foreach (var tag in itemDto.tags)
                        item.Tags.Add(tag);
                }


                playlist.Items.Add(item);
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