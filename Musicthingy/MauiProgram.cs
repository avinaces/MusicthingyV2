using CommunityToolkit.Maui;
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
            // Only initialize native VLC C++ libraries on Windows
            LibVLCSharp.Shared.Core.Initialize();
#endif

            var builder = MauiApp.CreateBuilder();

            // Register Services
            builder.Services.AddSingleton<ThemeManager>();
            // Use reflection-based automatic registration for app settings
            builder.Services.AddAppSettings();
            builder.Services.AddSingleton<ColorExtractionService>();

            // Replace this line:
            builder.Services.AddSingleton<YouTubeResolverService>();
            // With:
            builder.Services.AddHttpClient("youtube", c => {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(YouTubeResolverService.DefaultUserAgent);
            }).ConfigurePrimaryHttpMessageHandler(_ => YouTubeResolverService.Ipv4Handler)
              .SetHandlerLifetime(TimeSpan.FromMinutes(2));
            builder.Services.AddSingleton<YouTubeResolverService>(sp =>
                new YouTubeResolverService(sp.GetRequiredService<IHttpClientFactory>()));

            builder.Services.AddSingleton<Services.TrackCacheService>();


#if WINDOWS
            builder.Services.AddSingleton<LocalStreamProxy>();
            builder.Services.AddSingleton<Musicthingy.Services.IMediaProbingService, Musicthingy.Platforms.Windows.VlcMediaProbingService>();
#elif ANDROID
            builder.Services.AddSingleton<IMediaProbingService, Platforms.Android.ExoPlayerMediaProbingService>();
#endif

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
                .UseMauiCommunityToolkit(static options =>
                {
                    options.SetPopupDefaults(new DefaultPopupSettings
                    {
                        Padding = 0,
                    });

                    options.SetPopupOptionsDefaults(new DefaultPopupOptionsSettings
                    {
                        Shape = null,
                        CanBeDismissedByTappingOutsideOfPopup = true
                    });
                })
                .UseMPowerKitListView()
                .ConfigureFonts(fonts => { });


            return builder.Build();
        }
    }
}


public static class SettingsExtensions
{
    public static IServiceCollection AddAppSettings(this IServiceCollection services)
    {
        // 1. Find all classes that inherit from ISetting
        var settingTypes = System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(Musicthingy.Settings.ISetting).IsAssignableFrom(t));

        // 2. Register them automatically
        foreach (Type? type in settingTypes)
        {
            services.AddSingleton(typeof(Musicthingy.Settings.ISetting), type);
        }

        // 3. Register the manager
        services.AddSingleton<Musicthingy.Services.SettingsManager>();

        return services;
    }
}