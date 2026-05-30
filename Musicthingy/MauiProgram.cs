using CommunityToolkit.Maui;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using MPowerKit.VirtualizeListView;
using Musicthingy.Services;
using Musicthingy.ViewModels;

namespace Musicthingy
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
#if WINDOWS
            // Only initialize native VLC C++ libraries on Windows now
            LibVLCSharp.Shared.Core.Initialize();
#endif

            var builder = MauiApp.CreateBuilder();

            // Register Services
            builder.Services.AddSingleton<ThemeManager>();
            builder.Services.AddSingleton<YouTubeResolverService>();

            builder.Services.AddSingleton(sp => new HttpClient(YouTubeResolverService.Ipv4Handler));
            builder.Services.AddSingleton<LocalStreamProxy>();

            // ----------------------------------------------------
            // PLATFORM SPECIFIC PLAYBACK ENGINE INJECTION
            // ----------------------------------------------------
#if ANDROID
            // Use native ExoPlayer on Android
            builder.Services.AddSingleton<IMediaPlaybackService, Platforms.Android.ExoPlayerMediaPlaybackService>();
#elif WINDOWS
            // Use LibVLC on Windows
            builder.Services.AddSingleton<IMediaPlaybackService, VlcMediaPlaybackService>();
#endif
            // ----------------------------------------------------

            builder.Services.AddSingleton<MainViewModel>();

            // Register your platform-specific views
            builder.Services.AddTransient<Views.MobileMainPage>();
            builder.Services.AddTransient<AppShell>();

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