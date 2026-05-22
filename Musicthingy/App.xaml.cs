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