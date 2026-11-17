namespace Musicthingy.Models;

/// <summary>
/// The license a component is published under. The enum is used so the catalog and the
/// generated dependency list can agree without stringly-typed ids.
/// </summary>
public enum LicenseKind
{
    Mit,
    Apache2,
    Bsd2,
    Isc,
    Ofl11,
    Lgpl21,
    Gpl3,
    /// <summary>Microsoft .NET library license (no redistributable text).</summary>
    MsLibrary
}

/// <summary>A license: short id (badge text), full name, bundled text asset and canonical URL.</summary>
/// <param name="TextAsset">Path of the bundled text inside the app package, or null when the license has no bundled copy.</param>
public sealed record LicenseInfo(
    LicenseKind Kind,
    string Id,
    string Name,
    string? TextAsset,
    string Url);

/// <summary>One package inside a bundle entry (the transitive dependency group).</summary>
public sealed class LicensePackage
{
    public LicensePackage(string name, string version, LicenseKind kind, string? note = null)
    {
        Name = name;
        Version = version;
        Kind = kind;
        Note = note;
    }

    public string Name { get; }
    public string Version { get; }
    public LicenseKind Kind { get; }

    /// <summary>Extra context, e.g. "MIT .NET binding" for the Xamarin wrappers.</summary>
    public string? Note { get; }

    public string LicenseId => LicenseCatalog.GetKind(Kind).Id;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>One row on the About page: a library, an asset, or a bundle of packages.</summary>
public sealed class LicenseEntry
{
    public required string Name { get; init; }

    /// <summary>Two-character badge shown instead of a per-library icon.</summary>
    public required string Badge { get; init; }

    public string Version { get; init; } = string.Empty;
    public required string Summary { get; init; }
    public LicenseKind Kind { get; init; } = LicenseKind.Mit;
    public string? Copyright { get; init; }
    public string? ProjectUrl { get; init; }

    /// <summary>Non-empty for bundled groups (tap opens the package list).</summary>
    public IReadOnlyList<LicensePackage> Packages { get; init; } = [];

    public LicenseInfo License => LicenseCatalog.GetKind(Kind);
    public bool IsGroup => Packages.Count > 0;
    public string VersionLabel => string.IsNullOrEmpty(Version)
        ? string.Empty
        : IsGroup ? Version : "v" + Version;
    public string BadgeText => Badge;
}

/// <summary>One license text block on the detail page (a bundled group can have several).</summary>
public sealed record LicenseTextBlock(string Header, string Text);

/// <summary>A titled section of the About page. CollectionView grouping needs a list subclass.</summary>
public sealed class LicenseGroup : List<LicenseEntry>
{
    public LicenseGroup(string title, IEnumerable<LicenseEntry> entries) : base(entries)
    {
        Title = title;
    }

    public string Title { get; }
}
