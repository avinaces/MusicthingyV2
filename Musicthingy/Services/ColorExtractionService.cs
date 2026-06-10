using Musicthingy.Models;

namespace Musicthingy.Services;

public partial class ColorExtractionService
{
    // This partial method will be implemented differently on Android vs Windows
    public partial Task<Color?> ExtractDominantColorAsync(Playlist playlist);
}