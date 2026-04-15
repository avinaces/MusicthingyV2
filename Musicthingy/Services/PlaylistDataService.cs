using System.Text.Json;
using Musicthingy.Models;

namespace Musicthingy.Services;

public class PlaylistDataService
{
    public async Task<Playlist?> ImportPlaylistFromJsonAsync(string jsonContent)
    {
        try
        {
            // 1. Read into the safe DTO
            var dto = JsonSerializer.Deserialize<PlaylistDto>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return null;

            // 2. Map to UI Model
            var playlist = new Playlist
            {
                Name = dto.name,
                IconUrl = dto.icon,
                IconType = dto.iconType,
                Source = dto.source,
                CreatedAt = dto.createdAt ?? DateTime.Now,
                UpdatedAt = dto.updatedAt ?? DateTime.Now
            };

            foreach (var itemDto in dto.items)
            {
                var item = new PlaylistItem
                {
                    Title = string.IsNullOrEmpty(itemDto.title) ? "Unknown Title" : itemDto.title,
                    Url = itemDto.url,
                    StartTime = itemDto.startTime,
                    EndTime = itemDto.endTime,
                    IconUrl = itemDto.icon
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