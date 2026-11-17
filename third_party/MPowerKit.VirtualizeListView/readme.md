# MPowerKit.VirtualizeListView (vendored)

This folder is a copy of [MPowerKit.VirtualizeListView](https://github.com/MPowerKit/VirtualizeListView),
MIT licensed (see `LICENSE`), taken from the fork
[avinaces/VirtualizeListView](https://github.com/avinaces/VirtualizeListView) at commit
`b4c0879` ("Updated packages and some cache fixes", which includes `639937f autoscrolling`).

## Why it is vendored instead of referenced

The app needs the autoscroll fix in `Layouts/VirtualizeItemsLayoutManger.cs` (scroll-to-item
with a 1 px tolerance, a UI-thread yield between passes, and a failsafe break at the edge of
the scroll view). Upstream's NuGet package (`MPowerKit.VirtualizeListView` 2.8.0) does not
contain it, and the fork only replaces that one file.

Keeping the source in this repository means `git clone` (or a source ZIP) builds with no
submodule step, no second repository, and no network access. The trade-off is that updating
the library is a manual copy.

## Local changes to the upstream csproj

Only packaging/platform trimming, no source changes beyond the fork's fix:

- targets `net10.0-android` (plus the app's legacy `net10.0-windows10.0.19041.0` on Windows)
  instead of android/ios/maccatalyst/windows;
- `Microsoft.Maui.Controls` pinned to the app's version (`10.0.130`) - a split version makes
  the Windows TFM fail with PRI175/PRI277 because two MAUI PRI sets get merged;
- NuGet packing switched off (`GeneratePackageOnBuild` and pack-only `<None>` items removed).

## Updating

1. Copy the new `MPowerKit.VirtualizeListView/` sources over this folder, keeping the
   `TargetFrameworks` / `Microsoft.Maui.Controls` version edits above.
2. Re-apply the fork's autoscroll patch if upstream still has the `!=` loop (check
   `Layouts/VirtualizeItemsLayoutManger.cs`, `ScrollToPositionAsync`).
3. Build with `dotnet build Musicthingy/Musicthingy.csproj -f net10.0-android`.
