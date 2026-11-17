namespace Musicthingy.Models;

/// <summary>
/// Everything the About page shows: the licenses themselves, the components this app ships
/// and the dependencies it pulls in. Text files live in Resources/Raw/licenses so the page
/// works offline.
/// </summary>
public static class LicenseCatalog
{
    public const string RepoUrl = "https://github.com/avinaces/MusicthingyV2";

    private static readonly LicenseInfo Mit = new(
        LicenseKind.Mit,
        "MIT",
        "MIT License",
        "licenses/mit.txt",
        "https://opensource.org/license/mit");

    private static readonly LicenseInfo Apache2 = new(
        LicenseKind.Apache2,
        "Apache-2.0",
        "Apache License 2.0",
        "licenses/apache-2.0.txt",
        "https://www.apache.org/licenses/LICENSE-2.0");

    private static readonly LicenseInfo Bsd2 = new(
        LicenseKind.Bsd2,
        "BSD-2-Clause",
        "BSD 2-Clause \"Simplified\" License",
        "licenses/bsd-2-clause.txt",
        "https://opensource.org/license/bsd-2-clause");

    private static readonly LicenseInfo Isc = new(
        LicenseKind.Isc,
        "ISC",
        "ISC License",
        "licenses/isc.txt",
        "https://opensource.org/license/isc");

    private static readonly LicenseInfo Ofl11 = new(
        LicenseKind.Ofl11,
        "OFL-1.1",
        "SIL Open Font License 1.1",
        "licenses/ofl-1.1.txt",
        "https://openfontlicense.org");

    private static readonly LicenseInfo Lgpl21 = new(
        LicenseKind.Lgpl21,
        "LGPL-2.1",
        "GNU Lesser General Public License 2.1",
        "licenses/lgpl-2.1.txt",
        "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html");

    private static readonly LicenseInfo Gpl3 = new(
        LicenseKind.Gpl3,
        "GPL-3.0",
        "GNU General Public License 3.0",
        "licenses/gpl-3.0.txt",
        "https://www.gnu.org/licenses/gpl-3.0.html");

    private static readonly LicenseInfo MsLibrary = new(
        LicenseKind.MsLibrary,
        "MS-EULA",
        "Microsoft .NET Library License",
        null,
        "https://go.microsoft.com/fwlink/?linkid=865030");

    public static LicenseInfo GetKind(LicenseKind kind) => kind switch
    {
        LicenseKind.Mit => Mit,
        LicenseKind.Apache2 => Apache2,
        LicenseKind.Bsd2 => Bsd2,
        LicenseKind.Isc => Isc,
        LicenseKind.Ofl11 => Ofl11,
        LicenseKind.Lgpl21 => Lgpl21,
        LicenseKind.Gpl3 => Gpl3,
        _ => MsLibrary
    };

    /// <summary>"1.0 (1)" - app version and build number straight from the package.</summary>
    public static string AppVersion => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    /// <summary>The app itself: shown in the About page header and in the license list.</summary>
    public static LicenseEntry App => new()
    {
        Name = "Musicthingy",
        Badge = "M",
        Version = AppInfo.Current.VersionString,
        Summary = "This app. Free software: you may use, study, share and modify it.",
        Kind = LicenseKind.Gpl3,
        Copyright = "Copyright (C) 2026 avinaces",
        ProjectUrl = RepoUrl
    };

    /// <summary>Sections, in display order. Everything the app calls into is listed here.</summary>
    public static IReadOnlyList<LicenseGroup> BuildGroups()
    {
        return
        [
            new LicenseGroup("App", [App]),

            new LicenseGroup("Framework & UI",
            [
                new LicenseEntry
                {
                    Name = ".NET MAUI",
                    Badge = "MA",
                    Version = "10.0.130",
                    Summary = "Cross-platform UI framework behind every screen.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) .NET Foundation and Contributors",
                    ProjectUrl = "https://github.com/dotnet/maui"
                },
                new LicenseEntry
                {
                    Name = "CommunityToolkit.Maui",
                    Badge = "CT",
                    Version = "15.0.1",
                    Summary = "Maui helpers: icon tinting, file pickers, popups.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) .NET Foundation and Contributors",
                    ProjectUrl = "https://github.com/CommunityToolkit/Maui"
                },
                new LicenseEntry
                {
                    Name = "CommunityToolkit.Mvvm",
                    Badge = "MV",
                    Version = "8.4.2",
                    Summary = "Source-generated observable properties and commands.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) .NET Foundation and Contributors",
                    ProjectUrl = "https://github.com/CommunityToolkit/dotnet"
                },
                new LicenseEntry
                {
                    Name = "MPowerKit.VirtualizeListView",
                    Badge = "VL",
                    Version = "2.8.0",
                    Summary = "Virtualised list behind the track list. Vendored build with an autoscroll fix.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) 2024 MPowerKit",
                    ProjectUrl = "https://github.com/MPowerKit/VirtualizeListView"
                }
            ]),

            new LicenseGroup("Playback",
            [
                new LicenseEntry
                {
                    Name = "AndroidX Media3 (ExoPlayer)",
                    Badge = "M3",
                    Version = "1.10.1",
                    Summary = "Android media engine: buffering, decoding and the SABR data source.",
                    Kind = LicenseKind.Apache2,
                    Copyright = "Copyright (C) The Android Open Source Project",
                    ProjectUrl = "https://github.com/androidx/media"
                },
                new LicenseEntry
                {
                    Name = "LibVLCSharp",
                    Badge = "LC",
                    Version = "3.10.1",
                    Summary = "Managed bindings to libVLC; drives playback on the legacy Windows build.",
                    Kind = LicenseKind.Lgpl21,
                    Copyright = "Copyright (c) 2018-2025 VideoLAN",
                    ProjectUrl = "https://code.videolan.org/videolan/LibVLCSharp"
                },
                new LicenseEntry
                {
                    Name = "libVLC",
                    Badge = "VLC",
                    Version = "3.0.23",
                    Summary = "Native media library shipped by the VideoLAN.LibVLC.Windows package.",
                    Kind = LicenseKind.Lgpl21,
                    Copyright = "Copyright (c) 1996-2025 VideoLAN",
                    ProjectUrl = "https://www.videolan.org/vlc/libvlc.html"
                }
            ]),

            new LicenseGroup("YouTube",
            [
                new LicenseEntry
                {
                    Name = "YoutubeExplode",
                    Badge = "YT",
                    Version = "6.6.3",
                    Summary = "Metadata, playlists and stream resolution for YouTube.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) 2019-2025 Oleksii Holub",
                    ProjectUrl = "https://github.com/Tyrrrz/YoutubeExplode"
                },
                new LicenseEntry
                {
                    Name = "YoutubeExplode.Converter",
                    Badge = "YC",
                    Version = "6.6.3",
                    Summary = "Stream transcoding via FFmpeg, used for unsupported audio containers.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) 2019-2025 Oleksii Holub",
                    ProjectUrl = "https://github.com/Tyrrrz/YoutubeExplode"
                },
                new LicenseEntry
                {
                    Name = "bgutils-js",
                    Badge = "BG",
                    Version = "4.0.3",
                    Summary = "Bundled JavaScript that mints PO tokens inside the on-device WebView.",
                    Kind = LicenseKind.Mit,
                    Copyright = "Copyright (c) 2024 LuanRT",
                    ProjectUrl = "https://github.com/LuanRT/BgUtils"
                }
            ]),

            new LicenseGroup("Fonts & icons",
            [
                new LicenseEntry
                {
                    Name = "Lucide icons",
                    Badge = "LU",
                    Summary = "Line icon set tinted white for the whole interface.",
                    Kind = LicenseKind.Isc,
                    Copyright = "Copyright (c) Lucide Contributors",
                    ProjectUrl = "https://lucide.dev"
                },
                new LicenseEntry
                {
                    Name = "Roboto",
                    Badge = "RB",
                    Summary = "Variable UI font.",
                    Kind = LicenseKind.Apache2,
                    Copyright = "Copyright 2011 The Roboto Project Authors",
                    ProjectUrl = "https://fonts.google.com/specimen/Roboto"
                },
                new LicenseEntry
                {
                    Name = "Open Sans",
                    Badge = "OS",
                    Summary = "Fonts coming from the .NET MAUI project template.",
                    Kind = LicenseKind.Ofl11,
                    Copyright = "Copyright 2020 The Open Sans Project Authors",
                    ProjectUrl = "https://fonts.google.com/specimen/Open+Sans"
                }
            ]),

            new LicenseGroup("Transitive dependencies",
            [
                new LicenseEntry
                {
                    Name = "AndroidX, Google, Kotlin & Guava",
                    Badge = "AX",
                    Version = $"{ThirdPartyPackages.All.Count} packages",
                    Summary = "Pulled in by .NET MAUI and Media3; all of them ship inside the APK.",
                    Kind = LicenseKind.Apache2,
                    Copyright = "Copyright (C) The Android Open Source Project and contributors",
                    ProjectUrl = "https://github.com/dotnet/android",
                    Packages = ThirdPartyPackages.All
                }
            ])
        ];
    }
}
