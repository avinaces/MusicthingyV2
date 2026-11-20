using Android.App;
using Android.Runtime;
using AndroidX.AppCompat.App;

namespace Musicthingy
{
    [Application(AppCategory = Android.Content.PM.ApplicationCategories.Audio)]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            // Force the NATIVE (AppCompat) night mode, not just MAUI's theme.
            // MAUI's UserAppTheme only drives XAML bindings; without this, native
            // Android widgets (DisplayAlert, Picker, DatePicker, overflow menus,
            // ripples) still use the phone's light Material theme - and on
            // Android 8/9 there is no system dark mode to inherit at all.
            // Set before any Activity exists, so no Activity.Recreate() is needed.
            // ModeNightYes works on every supported API (26+), unlike
            // UiModeManager.SetApplicationNightMode (API 31+ only).
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightYes;
        }

        public override void OnCreate()
        {
            base.OnCreate();

            // Some OEM ROMs (MIUI/HyperOS, ColorOS, Funtouch) reset the delegate
            // during application startup; reassert it once the process is live.
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightYes;
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
