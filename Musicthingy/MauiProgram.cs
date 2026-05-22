using CommunityToolkit.Maui;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using MPowerKit.VirtualizeListView;
using Musicthingy.ViewModels;

namespace Musicthingy
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {

            // Initialize the native VLC C++ libraries
            Core.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder.Services.AddSingleton<MainViewModel>();
            builder.UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .UseMPowerKitListView()
                .ConfigureFonts(fonts => { });
#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}