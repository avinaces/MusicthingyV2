# Musicthingy

A Material 3 music player for Android that plays YouTube and local audio files in one library.

Musicthingy is a personal project: one MAUI app, no telemetry. It is not affiliated with, endorsed by, or supported by
YouTube or Google.

## Features

- **One library** — local files and YouTube videos side by side, with playlists, search and queue
  control (play next, shuffle, remove).
- **YouTube playback** through ExoPlayer with a multi-client resolver chain (VISIONOS → TVHTML5 → WEB).
- **Offline cache** — tracks you play are kept in a size-limited cache and replayed without
  the network.
- **Lyrics** with time-synced highlighting and auto-scroll.
- **Themes** — Material 3 dark palette, switchable accent colour or system theme.

## Requirements

| | |
|---|---|
| OS (dev) | Windows, macOS or Linux |
| SDK | [.NET 10 SDK](https://dotnet.microsoft.com/download) |
| Workload | `dotnet workload install android` (plus `maui-android`) |
| JDK | JDK 17 (Android toolchain) |
| Device | Android 8.0 (API 26) or newer |

The app also has a `net10.0-windows10.0.19041.0` target that still compiles, but it is legacy and
unmaintained: Android is the only supported platform.

## Build

```bash
dotnet build Musicthingy/Musicthingy.csproj -f net10.0-android
```

Always pass `-f net10.0-android`. Building through `Musicthingy.slnx` also evaluates the Windows
target, which needs the Windows App SDK workload.

To deploy to a connected device or emulator:

```bash
dotnet build Musicthingy/Musicthingy.csproj -f net10.0-android -t:Run
```

For a standalone APK that survives `adb install` (debug builds otherwise rely on fast
deployment):

```bash
dotnet build Musicthingy/Musicthingy.csproj -f net10.0-android \
  -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
```

## Signing

The repository contains no keystore and no passwords. Release signing is driven by
`Musicthingy/Signing.props`, which is git-ignored and imported only when present; a fresh clone
therefore builds unsigned, which is all a debug build needs. Create that file yourself with your
own keystore if you want to distribute a release build.

## Repository layout

```
Musicthingy/                     the MAUI app
  Platforms/Android/             ExoPlayer playback, SABR data source, PO token WebView
  Services/                      YouTube resolver, cache, settings, accounts
  Services/Sabr/                 SABR session handling (Android-only)
  Services/PoToken/              PO token generation (Android-only)
  ViewModels/                    MainViewModel (split by concern across partial files)
  Views/                         mobile UI, sheets, dialogs, About/licenses pages
  Settings/Definitions/          one class per setting, auto-registered by reflection
  Resources/Raw/licenses/        bundled license texts shown on the About page
third_party/MPowerKit.VirtualizeListView/
                                 vendored MIT fork of the virtualised list (see its README)
```

## Third-party licenses

Every library, font and icon set the app ships is listed on the in-app **Settings → General →
About & licenses** page, with the full license text bundled so it reads offline. The list is
generated from `obj/project.assets.json` plus a curated catalog in
`Musicthingy/Models/LicenseCatalog.cs`; regenerate `ThirdPartyPackages.cs` after adding or
removing packages.

## Known limitations

- YouTube changes its internals regularly. When playback fails, the app shows the resolver log
  from the last attempt — that log is the bug report.
- Playback of some videos requires a PO token; the built-in on-device generator is best-effort and
  can be replaced with an external provider URL in settings.
- Only Android is tested. The Windows target exists for historical reasons.

## License

Musicthingy is free software, licensed under the **GNU General Public License v3.0** — see
[LICENSE.txt](LICENSE.txt). Third-party components keep their own licenses, listed in the app.
