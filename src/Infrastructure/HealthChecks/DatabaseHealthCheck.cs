using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ecommerce.Db; 

// It's common practice for namespaces to be PascalCase, e.g., ecommerce.Infrastructure.
// However, using ecommerce.Infrastructure to match the using statements.
// Adjusted to a more specific namespace for health checks.
namespace ecommerce.Infrastructure.HealthChecks
{
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly ecommerceContext _ecommerceContext; // Type name 'ecommerceContext' might be a convention, usually PascalCase like 'ecommerceContext'
        private readonly ILogger<DatabaseHealthCheck> _logger;

        public DatabaseHealthCheck(
            ecommerceContext context, // Corrected: Added DbContext for DI
            ILogger<DatabaseHealthCheck> logger)
        {
             _ecommerceContext = context ?? throw new ArgumentNullException(nameof(context));
             _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext healthCheckContext, // Renamed to avoid conflict with DbContext instance
            CancellationToken cancellationToken = default)
        {
            var data = new Dictionary<string, object>();

            try
            {
                bool canConnect = await _ecommerceContext.Database.CanConnectAsync(cancellationToken);
                Exception? operationException = null;

                if (canConnect)
                {
                    try
                    {
                        // Execute a simple, fast query to ensure the database is responsive
                        await _ecommerceContext.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
                        data["DatabaseQueryCheck"] = "Successful";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Database connected but failed to execute a simple query ('SELECT 1').");
                        operationException = ex; // Query failed, but connection was possible
                        data["DatabaseQueryCheck"] = $"Failed: {ex.Message}";
                    }
                }
                else
                {
                    data["DatabaseQueryCheck"] = "Not attempted (cannot connect).";
                }

                // Gather detailed status information
                await PopulateDatabaseStatusData(data, cancellationToken, canConnect);
                await PopulateEntityCountsData(data, cancellationToken, canConnect);


                if (!canConnect)
                {
                    return new HealthCheckResult(
                        healthCheckContext.Registration.FailureStatus,
                        description: "Database connection failed.",
                        data: data);
                }
                if (operationException != null)
                {
                    return new HealthCheckResult(
                        healthCheckContext.Registration.FailureStatus,
                        description: "Database query failed after successful connection.",
                        exception: operationException,
                        data: data);
                }

                return HealthCheckResult.Healthy("Database operational and responsive.", data);
            }
            catch (Exception ex) // Catch unexpected exceptions during the health check process itself
            {
                _logger.LogError(ex, "Unexpected error during database health check procedure.");
                data["CriticalHealthCheckError"] = ex.Message;
                return new HealthCheckResult(
                    healthCheckContext.Registration.FailureStatus,
                    description: "Critical failure during database health check procedure.",
                    exception: ex,
                    data: data);
            }
        }

        private async Task PopulateDatabaseStatusData(
            Dictionary<string, object> data,
            CancellationToken cancellationToken,
            bool wasAbleToConnect)
        {
            var dbStatus = new Dictionary<string, object>
            {
                ["CanConnect"] = wasAbleToConnect,
                // Sanitize connection string to avoid exposing sensitive details
                ["DataSource"] = SanitizeConnectionString(_ecommerceContext.Database.GetDbConnection().ConnectionString)
            };

            if (wasAbleToConnect)
            {
                try
                {
                    var pendingMigrations = await _ecommerceContext.Database.GetPendingMigrationsAsync(cancellationToken);
                    dbStatus["PendingMigrations"] = pendingMigrations.Any() ? (object)pendingMigrations : "No pending migrations.";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve pending migrations.");
                    dbStatus["PendingMigrations"] = $"Error retrieving migrations: {ex.Message}";
                }
            }
            else
            {
                dbStatus["PendingMigrations"] = "Not checked (cannot connect).";
            }
            data["DatabaseInfo"] = dbStatus;
        }

        private async Task PopulateEntityCountsData(
            Dictionary<string, object> data,
            CancellationToken cancellationToken,
            bool canAccessDatabase) // Only attempt counts if DB is accessible
        {
            var entityCounts = new Dictionary<string, object>();
            if (canAccessDatabase) // Only attempt counts if we could connect and basic query worked
            {
                entityCounts["Products"] = await SafeCountAsync(_ecommerceContext.Products, "Products", cancellationToken);
                entityCounts["Orders"] = await SafeCountAsync(_ecommerceContext.Orders, "Orders", cancellationToken);
                entityCounts["Users"] = await SafeCountAsync(_ecommerceContext.UserProfiles, "UserProfiles", cancellationToken);
                entityCounts["Content"] = await SafeCountAsync(_ecommerceContext.Contents, "Contents", cancellationToken);
            }
            else
            {
                entityCounts["Products"] = "Not counted (cannot connect or query database).";
                entityCounts["Orders"] = "Not counted (cannot connect or query database).";
                entityCounts["Users"] = "Not counted (cannot connect or query database).";
                entityCounts["Content"] = "Not counted (cannot connect or query database).";
            }
            data["EntityCounts"] = entityCounts;
        }

        private async Task<object> SafeCountAsync<TEntity>(IQueryable<TEntity> queryable, string entityName, CancellationToken cancellationToken) where TEntity : class
        {
            try
            {
                return await queryable.CountAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to count entities for {EntityName}.", entityName);
                return $"Error counting {entityName}: {ex.Message}";
            }
        }

        private string SanitizeConnectionString(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return "Connection string not available.";
            }

            try
            {
                // Use DbConnectionStringBuilder to safely parse and modify the connection string
                var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };

                // Keywords to remove - case-insensitive by default for most providers
                string[] sensitiveKeywords = { "Password", "Pwd", "APIKey", "Secret", "User ID", "UID" };
                
                foreach (var keyword in sensitiveKeywords)
                {
                    if (builder.ContainsKey(keyword))
                    {
                        // For User ID, only remove if a password-like key was also present and removed.
                        // This is a simple heuristic; adjust based on your connection string formats.
                        if (keyword.Equals("User ID", StringComparison.OrdinalIgnoreCase) || keyword.Equals("UID", StringComparison.OrdinalIgnoreCase))
                        {
                            if (builder.ContainsKey("Password") || builder.ContainsKey("Pwd"))
                            {
                                // If password was already removed or wasn't there, keep User ID for now
                                // Or, decide to always remove User ID if it's considered sensitive alone.
                            }
                            else // Password key is not present, so we can remove User ID
                            {
                                builder.Remove(keyword);
                            }
                        }
                        else // For other sensitive keywords like Password, Pwd, APIKey, Secret
                        {
                             builder.Remove(keyword);
                        }
                    }
                }
                 // A common approach for User ID is to only remove it if a password was also present.
                // If Password/Pwd was removed (or never existed), then User ID might be less sensitive.
                // However, if you always want to hide User ID, you can simplify the logic.
                // Let's adjust: if Password was removed, then also try to remove User ID.
                bool passwordRemovedOrNotPresent = !(builder.ContainsKey("Password") || builder.ContainsKey("Pwd"));

                if (passwordRemovedOrNotPresent) // If no password, User ID might be less sensitive combined with server name
                {
                    // If User ID should be removed ONLY when Password is also removed/present.
                    // The previous loop already handles "Password" removal.
                    // If "Password" key was found and removed, then User ID is also removed if found.
                    // The logic for User ID removal can be complex depending on desired security.
                    // A simpler approach: if "Password" or "Pwd" was originally there, remove "User ID" / "UID" too.
                    // This needs the original keys before modification.
                    // For simplicity in this example, we'll stick to removing listed keys.
                    // A common scenario is to just show Server and Database name.
                }


                // Example: Retain only server and database for very basic info
                // var minimalInfoBuilder = new System.Data.Common.DbConnectionStringBuilder();
                // if (builder.TryGetValue("Server", out var server)) minimalInfoBuilder["Server"] = server;
                // if (builder.TryGetValue("Data Source", out var dataSource)) minimalInfoBuilder["Data Source"] = dataSource; // Alias for Server
                // if (builder.TryGetValue("Database", out var database)) minimalInfoBuilder["Database"] = database;
                // if (builder.TryGetValue("Initial Catalog", out var initialCatalog)) minimalInfoBuilder["Initial Catalog"] = initialCatalog; // Alias for Database
                // return minimalInfoBuilder.ConnectionString;

                return builder.ConnectionString; // Returns the string with sensitive keys removed
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sanitize connection string.");
                return "Unable to display connection string (sanitization failed).";
            }
        }
    }
}