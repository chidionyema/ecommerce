using Microsoft.AspNetCore.Builder;
using Serilog;
using Serilog.Events;

public static class LoggingExtensions
{
    public static void ConfigureLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, config) => 
            config.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File("/logs/app-log-.txt", rollingInterval: RollingInterval.Day));
    }
}