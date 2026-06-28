using Musicthingy.Settings;

namespace Musicthingy;

public static class SettingsExtensions
{
    public static IServiceCollection AddAppSettings(this IServiceCollection services)
    {
        // 1. Find all classes that inherit from ISetting
        var settingTypes = System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISetting).IsAssignableFrom(t));

        foreach (Type type in settingTypes)
        {
            // 2. Register for the UI List (IEnumerable<ISetting>)
            services.AddSingleton(typeof(ISetting), type);

            // 3. Register as ITSELF so ViewModels can inject them directly (e.g. inject ThemeColorSetting)
            services.AddSingleton(type);
        }

        // 4. Register the manager (now just a utility for mass-loading and UI grouping)
        services.AddSingleton<Services.SettingsManager>();

        return services;
    }
}