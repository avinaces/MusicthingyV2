using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Musicthingy;

internal static class ThemeResources
{
    public static Color Color(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object value) == true && value is Color color)
        {
            return color;
        }

        return Colors.Transparent;
    }
}
