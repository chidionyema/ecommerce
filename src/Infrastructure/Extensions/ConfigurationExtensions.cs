using Microsoft.Extensions.Configuration;

public static class ConfigurationExtensions
{
    public static void ConfigureEnvironmentVariables(this IConfigurationBuilder config)
    {
        config.AddEnvironmentVariables()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    }
}