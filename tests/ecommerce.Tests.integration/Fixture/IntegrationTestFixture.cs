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
using Xunit;

using ecommerce.Contracts;
using ecommerce.Db;
using ecommerce.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Respawn;

namespace ecommerce.Tests
{
    #region Core Test Infrastructure
   
    public class IntegrationTestFixture : IAsyncLifetime
    {
        private readonly DockerServiceManager _dockerManager;
        private Respawner _respawner;
        private readonly ILogger<IntegrationTestFixture> _logger;
        
        public CookieContainer CookieContainer { get; } = new CookieContainer();
        public CustomWebApplicationFactory Factory { get; }
        public IConfiguration Configuration { get; }
        private bool _databaseInitialized;

        public IntegrationTestFixture()
        {
            Configuration = ConfigurationLoader.LoadTestConfiguration(PathHelper.TestConfigPath);

            // Create an ILoggerFactory
            ILoggerFactory loggerFactoryForFixture = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactoryForFixture.CreateLogger<IntegrationTestFixture>();

            // Create logger for DockerHelper
            ILoggerFactory loggerFactoryForDocker = LoggerFactory.Create(builder => builder.AddConsole());
            var dockerLogger = loggerFactoryForDocker.CreateLogger<ecommerce.Tests.DockerHelper>();

            _dockerManager = new DockerServiceManager(Configuration, loggerFactoryForDocker);
            Factory = new CustomWebApplicationFactory(PathHelper.TestConfigPath);
       }

        public async Task InitializeAsync()
        {
            await _dockerManager.StartAllServicesAsync();
            await InitializeDatabasesAsync();
            await CreateRespawnCheckpointAsync();
        }       
    
        public async Task DisposeAsync() => await _dockerManager.StopAllServicesAsync();

        public HttpClient CreateClientWithCookies() =>
            HttpClientFactory.CreateWithCookies(Factory, CookieContainer);

        private async Task InitializeDatabasesAsync()
        {  
            if (_databaseInitialized) return;
            using var scope = Factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
        
            NpgsqlConnection.ClearAllPools();

            // Migrate and seed Identity (roles) database
            var ecommerceContext = sp.GetRequiredService<ecommerceContext>();
            await ecommerceContext.Database.EnsureDeletedAsync();
            await ecommerceContext.Database.MigrateAsync();
            await SeedRolesAsync(sp);

            // Migrate the content database
            ////var contentContext = sp.GetRequiredService<ContentContext>();
            //await contentContext.Database.MigrateAsync();

            _databaseInitialized = true;
        }

        // Seeds the roles directly after migration.
        private async Task SeedRolesAsync(IServiceProvider sp)
        {
            var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
            string[] roles = { "Admin", "User", "ContentUploader" };

            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    var result = await roleManager.CreateAsync(new IdentityRole(role));
                    if (result.Succeeded)
                    {
                        _logger.LogInformation("Seeded role: {Role}", role);
                    }
                    else
                    {
                        _logger.LogError("Failed to seed role {Role}: {Errors}", role,
                            string.Join(", ", result.Errors.Select(e => e.Description)));
                    }
                }
            }
        }

        private async Task CreateRespawnCheckpointAsync()
        {
            using var connection = new NpgsqlConnection(Configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = new[] { "public" },
                // Prevent deletion of role-related data by ignoring these tables.
                TablesToIgnore = new[]
                {
                    new Respawn.Graph.Table("public", "__EFMigrationsHistory"),
                    new Respawn.Graph.Table("public", "AspNetRoles"),
                    new Respawn.Graph.Table("public", "AspNetUserRoles"),
                    // Optionally, ignore claims if you want to preserve them as well.
                    new Respawn.Graph.Table("public", "AspNetRoleClaims")
                },
                WithReseed = true
            });
}


        public async Task ResetDatabaseAsync()
        {
            using var connection = new NpgsqlConnection(Configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();
            await _respawner.ResetAsync(connection);

            // Reset Redis.
            // Update the connection string to include allowAdmin=true.
            string redisConnectionString = Configuration.GetConnectionString("Redis");
            if (!redisConnectionString.Contains("allowAdmin=true"))
            {
                redisConnectionString += ",allowAdmin=true";
            }
            var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            await redis.GetServer(redis.GetEndPoints().First()).FlushAllDatabasesAsync();
        }

        public HttpClient CreateAuthorizedClient(string token) =>
            HttpClientFactory.CreateAuthorized(Factory, token);

        public HttpClient CreateBypassAuthClient() =>
            HttpClientFactory.CreateBypassAuth(Factory);
    }
    #endregion


    public static class PathHelper
    {
        public static string TestConfigPath => FindParentContaining("appsettings.Test.json");

        private static string FindParentContaining(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !directory.GetFiles(fileName).Any())
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? throw new FileNotFoundException(fileName);
        }
    }

    public static class ConfigurationLoader
    {
        public static IConfigurationRoot LoadTestConfiguration(string path) =>
            new ConfigurationBuilder()
                .SetBasePath(path)
                .AddJsonFile("appsettings.Test.json")
                .Build();
    }

    public class AllowAllHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (var requirement in context.PendingRequirements)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
    
    [CollectionDefinition("Integration Tests", DisableParallelization = true)]
    public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture> { }
}
