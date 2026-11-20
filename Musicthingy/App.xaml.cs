using Microsoft.Extensions.DependencyInjection;
using System;

namespace Musicthingy
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            InitializeComponent();

            // The app is dark-only by design (Colors.xaml is a dark palette and
            // there is no light variant). Pinning the MAUI theme keeps every
            // AppThemeBinding/dynamic resource on the dark values even on
            // devices whose system theme is light - and on Android 8/9 phones
            // that have no system dark-mode toggle at all. Must run before the
            // first window is created; UserAppTheme is a runtime property, so it
            // is not persisted and cannot leak a light value from a prior run.
            UserAppTheme = AppTheme.Dark;

            _services = services;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Route the UI based on the platform right when the window is created,
            // using the DI container to cleanly resolve views and their ViewModels.
#if ANDROID
            Views.MobileMainPage page = _services.GetRequiredService<Views.MobileMainPage>();
            return new Window(page);
#else
            AppShell shell = _services.GetRequiredService<AppShell>();
            return new Window(shell);
#endif
        }
    }
}