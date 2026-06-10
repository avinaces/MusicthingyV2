using Musicthingy.Models;

namespace Musicthingy.Services;

public partial class ColorExtractionService
{
    public partial Task<Microsoft.Maui.Graphics.Color?> ExtractDominantColorAsync(Playlist playlist)
    {
        // Fallback for Windows. You could implement WinRT color extraction later.
        return Task.FromResult<Microsoft.Maui.Graphics.Color?>(null);
    }
}