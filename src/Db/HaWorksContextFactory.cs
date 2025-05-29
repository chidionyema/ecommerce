using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using ecommerce.Services;
using Microsoft.Extensions.Options;
using ecommerce.Db;

namespace ecommerce.Db
{
    public class ecommerceContextFactory : IDesignTimeDbContextFactory<ecommerceContext>
    {
        public ecommerceContext CreateDbContext(string[] args)
        {
            // Build configuration from the current directory.
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            // Determine the current environment.
            string env = config["ASPNETCORE_ENVIRONMENT"] ?? "Development";
            string connectionString = string.Empty;

            if (env.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                // For local development, use the default connection string.
                connectionString = config.GetConnectionString("DefaultConnection")!;
                Console.WriteLine($"[Debug] Environment is '{env}'. Using default connection string from configuration.");
            }
            else
            {
                // In production, attempt to retrieve the connection string via Vault.
                var roleIdPath = config["Vault:RoleIdPath"];
                var secretIdPath = config["Vault:SecretIdPath"];

                if (!string.IsNullOrEmpty(roleIdPath) && File.Exists(roleIdPath) &&
                    !string.IsNullOrEmpty(secretIdPath) && File.Exists(secretIdPath))
                {
                    try
                    {
                        // Create a logger for VaultService.
                        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
                        var vaultLogger = loggerFactory.CreateLogger<VaultService>();

                        // Create Options objects for VaultOptions and DatabaseOptions.
                        var vaultOptions = Options.Create(new VaultOptions
                        {
                            Address = config["Vault:Address"] ?? string.Empty,
                            RoleIdPath = config["Vault:RoleIdPath"] ?? string.Empty,
                            SecretIdPath = config["Vault:SecretIdPath"] ?? string.Empty,
                            CertThumbprint = config["Vault:CertThumbprint"] ?? string.Empty
                        });
                        var databaseOptions = Options.Create(new DatabaseOptions
                        {
                            Host = config["Database:Host"] ?? string.Empty
                        });

                        // Create VaultService with the proper options.
                        var vaultService = new VaultService(vaultOptions, databaseOptions, vaultLogger);
                        connectionString = vaultService.GetDatabaseConnectionStringAsync().Result!;
                        Console.WriteLine("[Debug] Retrieved connection string from Vault.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to retrieve connection string from Vault: {ex.Message}");
                        // Fall back to default connection string.
                        connectionString = config.GetConnectionString("DefaultConnection")!;
                        Console.WriteLine("[Debug] Using default connection string from configuration as fallback.");
                    }
                }
                else
                {
                    // Fallback if Vault files are missing.
                    connectionString = config.GetConnectionString("DefaultConnection")!;
                    Console.WriteLine("[Debug] Vault files not found. Using default connection string from configuration.");
                }
            }

            // Configure the DbContext options.
            var optionsBuilder = new DbContextOptionsBuilder<ecommerceContext>();
            optionsBuilder
                .UseNpgsql(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .EnableDetailedErrors();

            // Create dependencies.
            var loggerFactoryReal = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactoryReal.CreateLogger<ecommerceContext>();

            var httpContextAccessor = new HttpContextAccessor();
            var currentUserService = new CurrentUserService(httpContextAccessor);
            var hostEnvironment = new DesignTimeHostEnvironment();

            return new ecommerceContext(
                optionsBuilder.Options,
                currentUserService,
                logger,
                httpContextAccessor,
                config,
                hostEnvironment);
        }
    }

    // Minimal implementation of IHostEnvironment for design time.
    public class DesignTimeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ecommerce";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(Directory.GetCurrentDirectory());
    }
}
