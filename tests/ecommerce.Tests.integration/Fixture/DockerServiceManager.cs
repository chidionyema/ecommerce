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
using Respawn;

namespace ecommerce.Tests
{
    public class DockerServiceManager
    {
        private readonly List<DockerService> _services;
        private readonly ILogger _logger;

        public DockerServiceManager(IConfiguration config, ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<DockerServiceManager>();
            _services = new List<DockerService>
            {
                new PostgresService(config, loggerFactory),
                new RedisService(config, loggerFactory),
                new MinioService(config, loggerFactory)
            };
        }

        public async Task StartAllServicesAsync()
        {
            foreach (var service in _services)
            {
                await service.StartAsync();
                await service.WaitForReadyAsync();
            }
        }

        public async Task StopAllServicesAsync()
        {
            foreach (var service in _services.AsEnumerable().Reverse())
            {
                await service.StopAsync();
            }
        }
    }

    public abstract class DockerService
    {
        protected readonly IConfiguration _config;
        protected readonly ILogger _logger;
        protected readonly DockerHelper _helper;

        protected DockerService(
            IConfiguration config,
            ILoggerFactory loggerFactory, // Accept ILoggerFactory instead of ILogger
            string imageKey,
            string containerKey,
            string defaultImage)
        {
            _config = config;
            _logger = loggerFactory.CreateLogger(GetType()); // Create logger for the service
            var dockerLogger = loggerFactory.CreateLogger<DockerHelper>(); // Create DockerHelper logger
            _helper = new DockerHelper(
                dockerLogger,
                _config[$"Docker:{imageKey}"] ?? defaultImage,
                _config[$"Docker:{containerKey}"] ?? $"{imageKey}_test");
        }

        public abstract Task StartAsync();
        public abstract Task WaitForReadyAsync();
        public Task StopAsync() => _helper.StopContainer();
    }

    public class PostgresService : DockerService
    {
        public PostgresService(IConfiguration config, ILoggerFactory loggerFactory) 
            : base(config, loggerFactory, "PostgresImage", "PostgresContainer", "postgres:13") { }

        // In PostgresService.cs
        public override async Task StartAsync()
        {
            await _helper.StartContainer(new ContainerParameters 
            {
                HostPort = _config.GetValue<int>("Docker:PostgresPort", 5433),
                ContainerPort = 5432,
                EnvVars = new List<string>
                {
                    $"POSTGRES_USER={_config["Database:User"]}",
                    $"POSTGRES_PASSWORD={_config["Database:Password"]}"
                },
                HealthCheck = HealthCheckConfig.Postgres(_config["Database:User"] ?? "postgres")
            });
        }

        public override async Task WaitForReadyAsync()
        {
            var connectionString = $"Host=localhost;Port={_helper.HostPort};" +
                $"Username={_config["Database:User"]};Password={_config["Database:Password"]};";
            
            await DatabaseMaintainer.EnsureCreatedAsync(connectionString, _logger);
        }
    }

    public class RedisService : DockerService
    {
        public RedisService(IConfiguration config, ILoggerFactory loggerFactory)
            : base(config, loggerFactory, "RedisImage", "RedisContainer", "redis:latest") { }

        public override async Task StartAsync()
        {
            await _helper.StartContainer(
                new ContainerParameters{
                    HostPort = _config.GetValue<int>("Docker:RedisPort", 6380),
                    ContainerPort = 6379,
                    HealthCheck = HealthCheckConfig.Redis()}
            );
        }

        public override async Task WaitForReadyAsync()
        {
            await ConnectionMultiplexer.ConnectAsync(
                $"localhost:{_helper.HostPort},abortConnect=false");
        }
    }

    public class MinioService : DockerService
    {
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly int _hostApiPort;       // Port for API (default 9000 in MinIO)
        private readonly int _hostConsolePort;   // Port for Console (default 9001 in MinIO if enabled)

        public MinioService(IConfiguration config, ILoggerFactory loggerFactory)
            : base(config, loggerFactory, 
                   "MinioImage",      // Key in config for MinIO image name
                   "MinioContainer",  // Key in config for MinIO container name
                   "minio/minio:latest") // Default image if not in config
        {
            _accessKey = _config["MinIO:AccessKey"] ?? throw new InvalidOperationException("MinIO:AccessKey is required in configuration.");
            _secretKey = _config["MinIO:SecretKey"] ?? throw new InvalidOperationException("MinIO:SecretKey is required in configuration.");
            
            _hostApiPort = _config.GetValue<int>("Docker:MinioApiPort", 9000); 
            _hostConsolePort = _config.GetValue<int>("Docker:MinioConsolePort", 9001); // Often separate from API port

            _logger.LogInformation(
                "MinioService configured. API Port (Host): {ApiPort}, Console Port (Host): {ConsolePort}, AccessKey: {AccessKey}", 
                _hostApiPort, _hostConsolePort, _accessKey);
        }

        public string AccessKey => _accessKey;
        public string SecretKey => _secretKey;
        public int ApiPort => _hostApiPort; 
        public int ConsolePort => _hostConsolePort;

        public override async Task StartAsync()
        {
            string minioImage = _config["Docker:MinioImage"] ?? "minio/minio:latest";
            _logger.LogInformation(
                "Starting MinIO container '{ContainerName}' with image '{ImageName}'. " +
                "Host API Port: {HostApiPort} -> Container API Port: 9000. " +
                "Host Console Port: {HostConsolePort} -> Container Console Port: 9001. " +
                "AccessKey: {AccessKey}",
                _helper.ContainerName, minioImage, _hostApiPort, _hostConsolePort, _accessKey);

            var containerParams = new ContainerParameters
            {
                HostPort = _hostApiPort, // This will be mapped by DockerHelper to ContainerPort
                ContainerPort = 9000,    // MinIO's API port inside the container
                Command = new List<string> { "server", "/data", "--console-address", ":9001" }, // Explicit command
                EnvVars = new List<string>
                {
                    $"MINIO_ROOT_USER={_accessKey}",
                    $"MINIO_ROOT_PASSWORD={_secretKey}"
                },
                // HealthCheck = HealthCheckConfig.Minio() // <<--- REMOVED THIS LINE
                // By removing the custom HealthCheck, Docker will use the health check
                // defined within the minio/minio image itself, which is generally reliable.
            };

            // If your DockerHelper needs to map multiple ports (e.g., 9000 for API, 9001 for console)
            // it needs to be adapted. The current ContainerParameters takes a single HostPort/ContainerPort.
            // For now, we're focusing on the API port (9000) which _helper.StartContainer will use.
            // To map the console port as well, DockerHelper.StartContainer's HostConfig would need:
            // PortBindings = new Dictionary<string, IList<PortBinding>>
            // {
            //     ["9000/tcp"] = new List<PortBinding> { new PortBinding { HostPort = _hostApiPort.ToString() } },
            //     ["9001/tcp"] = new List<PortBinding> { new PortBinding { HostPort = _hostConsolePort.ToString() } }
            // }
            // And ContainerParameters would need to be adjusted to pass this dictionary.
            // The current single HostPort parameter in ContainerParameters will map to containerParams.ContainerPort (9000).

            await _helper.StartContainer(containerParams);
            _logger.LogInformation("MinIO container '{ContainerName}' start process initiated.", _helper.ContainerName);
        }

        public override async Task WaitForReadyAsync()
        {
            // _helper.HostPort should reflect the host port mapped to the container's API port (9000)
            _logger.LogInformation("Waiting for MinIO service to be ready at localhost:{ApiPort}...", _helper.HostPort); 
            
            var minioClient = CreateClient();

            var retryPolicy = Policy
                .Handle<Exception>() 
                .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (exception, timeSpan, retryCount, context) =>
                    {
                        _logger.LogWarning(exception, "MinIO readiness check (ListBucketsAsync) failed. Retrying in {TimeSpanTotalSeconds}s... (Attempt {RetryCount}/5)", timeSpan.TotalSeconds, retryCount);
                    });

            await retryPolicy.ExecuteAsync(async () =>
            {
                await minioClient.ListBucketsAsync(); 
                _logger.LogInformation("MinIO API is responsive (ListBucketsAsync successful).");
            });

            await EnsureBucketsExistAsync(minioClient);
            
            _logger.LogInformation(
                "MinIO is ready. API Endpoint: localhost:{ApiPort}. Console likely on localhost:{ConsolePort} (if mapped). AccessKey: {AccessKey}", 
                _helper.HostPort, _hostConsolePort, _accessKey);
        }
        
        public IMinioClient CreateClient()
        {
            // _helper.HostPort is the host port mapped to the container's API port (9000) by DockerHelper
            return new MinioClient()
                .WithEndpoint($"localhost:{_helper.HostPort}") 
                .WithCredentials(_accessKey, _secretKey)
                .WithSSL(false) // Common for local Docker testing
                .Build();
        }

        private async Task EnsureBucketsExistAsync(IMinioClient client)
        {
            var requiredBuckets = new[] { "temp-chunks", "final-content" }; 
            _logger.LogInformation("Ensuring required MinIO buckets exist: [{Buckets}]", string.Join(", ", requiredBuckets));
            
            foreach (var bucketName in requiredBuckets)
            {
                try 
                {
                    var bucketExistsArgs = new BucketExistsArgs().WithBucket(bucketName);
                    bool exists = await client.BucketExistsAsync(bucketExistsArgs);
                        
                    if (!exists)
                    {
                        _logger.LogInformation("Creating MinIO bucket: {BucketName}", bucketName);
                        var makeBucketArgs = new MakeBucketArgs().WithBucket(bucketName);
                        await client.MakeBucketAsync(makeBucketArgs);
                            
                        // WARNING: This is a very permissive policy for testing ONLY.
                        var policy = $@"{{
                            ""Version"": ""2012-10-17"",
                            ""Statement"": [
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Principal"": {{""AWS"": [""*""]}},
                                    ""Action"": [""s3:GetObject"", ""s3:PutObject"", ""s3:DeleteObject"", ""s3:ListBucket""],
                                    ""Resource"": [""arn:aws:s3:::{bucketName}/*"", ""arn:aws:s3:::{bucketName}""]
                                }}
                            ]
                        }}";
                        
                        var setPolicyArgs = new SetPolicyArgs().WithBucket(bucketName).WithPolicy(policy);
                        await client.SetPolicyAsync(setPolicyArgs);
                        
                        _logger.LogInformation("Created MinIO bucket '{BucketName}' with permissive test policy.", bucketName);
                    }
                    else 
                    {
                        _logger.LogInformation("MinIO Bucket '{BucketName}' already exists.", bucketName);
                    }
                }
                catch (Exception ex) 
                {
                    _logger.LogError(ex, "Error ensuring MinIO bucket '{BucketName}' exists. This can cause test failures if the bucket is essential.", bucketName);
                    throw; // Rethrow as this is a critical setup step
                }
            }
        }
    }
       public static class AuthorizationPolicies
    {
        public const string ContentUploader = "ContentUploader";
    }

    public static class UserRoles
    {
        public const string ContentUploader = "ContentUploader";
    }

    public static class HealthCheckConfig
    {
        public static HealthConfig Postgres(string user) => new HealthConfig
        {
            Test = new List<string> { "CMD-SHELL", $"pg_isready -U {user}" },
            Interval = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromSeconds(1),
            Retries = 20
        };

        public static HealthConfig Redis() => new HealthConfig
        {
            Test = new List<string> { "CMD-SHELL", "redis-cli ping | grep PONG" },
            Interval = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 20
        };

        public static HealthConfig Minio() => new HealthConfig
        {
            Test = new List<string> { "CMD-SHELL", "mc admin health check" },
            Interval = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 20
        };
    }

    public static class DatabaseMaintainer
    {
        private static readonly AsyncRetryPolicy _retryPolicy = Policy
            .Handle<NpgsqlException>()
            .WaitAndRetryAsync(5, attempt => 
                TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        public static async Task EnsureCreatedAsync(string connectionString, ILogger logger)
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                var exists = await CheckDatabaseExists(connection);
                if (!exists) await CreateDatabase(connection, logger);
                
                await GrantPrivileges(connection, logger);
            });
        }

        private static async Task CreateDatabase(NpgsqlConnection connection, ILogger logger)
        {
            var user = new NpgsqlConnectionStringBuilder(connection.ConnectionString).Username;
            logger.LogInformation("Creating test database owned by {User}", user);
            await new NpgsqlCommand($"CREATE DATABASE test_db OWNER \"{user}\"", connection)
                .ExecuteNonQueryAsync();
        }

          private static async Task<bool> CheckDatabaseExists(NpgsqlConnection connection)
        {
            var cmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = 'test_db'", connection);
            return await cmd.ExecuteScalarAsync() != null;
        }


    private static async Task GrantPrivileges(NpgsqlConnection connection, ILogger logger)
    {
        logger.LogInformation("Configuring database privileges");
        var user = new NpgsqlConnectionStringBuilder(connection.ConnectionString).Username;
        var query = $@"
            ALTER USER ""{user}"" CREATEDB;
            ALTER USER ""{user}"" WITH SUPERUSER;"; // Grant superuser for testing ONLY
        await new NpgsqlCommand(query, connection).ExecuteNonQueryAsync();
    }
    
    public static async Task ResetAsync(string connectionString, ILogger logger)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    // Get tables EXCLUDING role-related tables
    var tables = await GetNonRoleTables(connection);
    
    var truncateSql = $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE;";
    await new NpgsqlCommand(truncateSql, connection).ExecuteNonQueryAsync();
}

    private static async Task<List<string>> GetNonRoleTables(NpgsqlConnection connection)
    {
        var tables = new List<string>();
        var cmd = new NpgsqlCommand(
            @"SELECT table_name 
            FROM information_schema.tables 
            WHERE table_schema = 'public' 
            AND table_name NOT IN ('AspNetRoles', 'AspNetRoleClaims')", 
            connection);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add($"\"{reader.GetString(0)}\"");
        }
        return tables;
    }
  
  }
    
    
}
