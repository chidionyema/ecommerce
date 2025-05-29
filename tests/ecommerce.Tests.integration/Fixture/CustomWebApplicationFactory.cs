using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Minio.Exceptions;
using Minio.DataModel.Args;
using Npgsql;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Swagger;
using AspNetCoreRateLimit;

using Xunit;

using ecommerce.Contracts;
using ecommerce.Db;
using ecommerce.Services;
using Microsoft.AspNetCore.Http;
using Respawn;

namespace ecommerce.Tests
{
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _testConfigPath;
        private readonly IConfigurationRoot _testConfiguration;

        public CustomWebApplicationFactory(string testConfigPath)
        {
            _testConfigPath = testConfigPath;
            _testConfiguration = ConfigurationLoader.LoadTestConfiguration(testConfigPath);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging
                .ClearProviders()
                .AddConsole()
                .AddDebug()
                .SetMinimumLevel(LogLevel.Debug));

            ConfigureAuthentication(builder);
            ConfigureEnvironmentAndConfiguration(builder);
            ConfigureTestServices(builder);
            
            builder.ConfigureServices(services => 
            {
                var serviceProvider = services.BuildServiceProvider();
                using var scope = serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;
                
                try
                {   
                    services.AddAntiforgery(options => 
                    {
                        options.SuppressXFrameOptionsHeader = true;
                        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        options.HeaderName = "X-CSRF-TOKEN";
                    });
                    
                    services.AddControllers()
                        .AddApplicationPart(typeof(TestAuthController).Assembly)
                        .AddApplicationPart(typeof(ecommerce.Controllers.ContentController).Assembly)
                        .AddControllersAsServices();

                    // Clear connections to prevent "in use" errors
                    NpgsqlConnection.ClearAllPools();

                    // Get the database contexts
                    var ecommerceContext = sp.GetRequiredService<ecommerceContext>();
                    //var contentContext = sp.GetRequiredService<ContentContext>();
                    //var productContext = sp.GetRequiredService<ProductContext>();

                    var logger = sp.GetRequiredService<ILogger<CustomWebApplicationFactory>>();
                    
                    // For tests, recreate the database from scratch
                    ecommerceContext.Database.EnsureDeleted();
                    //contentContext.Database.EnsureDeleted();
                    //productContext.Database.EnsureDeleted();
                    
                    // Create fresh schemas - no migrations
                    ecommerceContext.Database.EnsureCreated();
                   // contentContext.Database.EnsureCreated();
                    //productContext.Database.EnsureCreated();

                    logger.LogInformation("Verifying database schema creation...");
                    var tables = ecommerceContext.Model.GetEntityTypes()
                        .Select(t => t.GetTableName())
                        .Where(t => t != null)
                        .ToList();
                    logger.LogInformation("Created tables: {Tables}", string.Join(", ", tables));
                }
                catch (Exception ex)
                {
                    var logger = sp.GetRequiredService<ILogger<CustomWebApplicationFactory>>();
                    logger.LogError(ex, "Database migration failed.");
                    throw;
                }
            });
        }

        private void ConfigureAuthentication(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.AddTransient<TestAuthMiddleware>();
                services.ConfigureJwtBearer(_testConfiguration);
            });
        }

        private void ConfigureEnvironmentAndConfiguration(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test")
                   .UseContentRoot(_testConfigPath)
                   .ConfigureAppConfiguration(config => 
                       config.AddConfiguration(_testConfiguration));
        }
     private void ConfigureTestServices(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
     {  
        services.AddHttpContextAccessor();
        services.RemoveSwaggerGenerator()
                .RemoveAll<IVaultService>()
                .ConfigureTestAuthorization()
                .ReplaceService<IVaultService, FakeVaultService>(sp => new FakeVaultService())
                .AddSingleton<DynamicCredentialsConnectionInterceptor>()
                .ReplaceService<IVirusScanner, FakeVirusScanner>(sp => new FakeVirusScanner())
                .ConfigureContentStorage(_testConfiguration)
                .ConfigureDatabases(_testConfiguration)
                .ConfigureRedis(_testConfiguration);

        // Remove AspNetCoreRateLimit components
        services.RemoveAll<IIpPolicyStore>();
        services.RemoveAll<IClientPolicyStore>();
        services.RemoveAll<IRateLimitCounterStore>();
        services.RemoveAll<IRateLimitConfiguration>();
        services.RemoveAll<IProcessingStrategy>();
        
        // Remove .NET Core built-in rate limiting components
        services.RemoveAll<Microsoft.AspNetCore.RateLimiting.IRateLimiterPolicy<string>>();
        services.RemoveAll<Microsoft.AspNetCore.RateLimiting.IRateLimiterPolicy<object>>();
      
       
    });
}
    }
}