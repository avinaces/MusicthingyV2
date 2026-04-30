using Microsoft.Extensions.DependencyInjection;

namespace Musicthingy
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Route the UI based on the platform right when the window is created
#if ANDROID
            return new Window(new Views.MobileMainPage());
#else
            // For Windows, use your existing AppShell
            return new Window(new AppShell());
#endif
        }
    }
}