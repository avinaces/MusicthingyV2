using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Musicthingy.Helpers
{
    public class NaturalTitleComparer : IComparer<string?>
    {
        public static NaturalTitleComparer Instance { get; } = new NaturalTitleComparer();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            // Trim via Span prevents allocating new strings during sorting
            ReadOnlySpan<char> spanX = x.AsSpan().Trim();
            ReadOnlySpan<char> spanY = y.AsSpan().Trim();

            // CompareOptions.NumericOrdering is a .NET 10 feature that naturally sorts numbers
            return CultureInfo.CurrentCulture.CompareInfo.Compare(
                spanX,
                spanY,
                CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        }
    }
}
