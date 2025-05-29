using ecommerce.Extensions;
using ecommerce.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ecommerce.Infrastructure.Middleware;
using ecommerce.Extensions;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(args);

            // Use the logging configuration defined in the LoggingExtensions
            builder.ConfigureLogging();

            // Load production configuration (tests will override this later via environment variable and factory)
            var env = builder.Environment;
            builder.Configuration
                .AddEnvironmentVariables()
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);

            // Register MVC controllers.
            builder.Services.AddControllers();

            // Register Swagger services.
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = "My API",
                    Version = "v1"
                });
            });

            // Create a logger instance specifically for infrastructure registration.
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog();
            });
            var infraLogger = loggerFactory.CreateLogger("InfrastructureServices");

            // Register infrastructure and security services.
            builder.Services.AddInfrastructureServices(builder.Configuration, infraLogger);
            builder.Services.AddSecurityServices(builder.Configuration);
            builder.Services.AddApplicationServices();
            builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));


            var app = builder.Build();
            await app.SeedRolesAsync(); 

            // Initialize Vault only when not running tests.
            if (!IsTestHost())
            {
                using (var scope = app.Services.CreateScope())
                {
                    var vault = scope.ServiceProvider.GetRequiredService<IVaultService>();
                    await vault.InitializeAsync();
                }
            }  

            // Configure middleware pipeline.
            app.ConfigureMiddlewarePipeline();
            app.UseEnhancedSecurityHeaders(builder.Configuration);

            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Determines whether the current process is running as a test host.
    /// WebApplicationFactory creates a test host whose AppDomain friendly name typically starts with "testhost".
    /// </summary>
    private static bool IsTestHost()
    {
        return AppDomain.CurrentDomain.FriendlyName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase);
    }
}
