using ecommerce.Contracts;
using ecommerce.Db;
using ecommerce.Services; // Assuming this contains ClamAVScanner, FileSignatureValidator etc.
using Microsoft.EntityFrameworkCore;
using ecommerce.Webhooks;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minio;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using Stripe;
using Stripe.Checkout; // If SessionService is from here.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using ecommerce.Infrastructure.Repository.Interfaces;
using ecommerce.Infrastructure.Repository;
using ecommerce.Infrastructure.HealthChecks;
using System.Threading.Tasks;
// Assuming your repository interfaces (IProductRepository etc.) and implementations (ProductRepository etc.)
// are in a namespace like ecommerce.Infrastructure.Repositories or directly under ecommerce.Infrastructure
using ecommerce.Infrastructure; // For repositories, adjust if namespace is different

namespace ecommerce.Extensions
{
    public static class InfrastructureExtensions
    {
        public static IServiceCollection AddInfrastructureServices(
            this IServiceCollection services,
            IConfiguration config,
            ILogger logger) // Logger from the calling context (e.g., Program.cs)
        {
            bool isTestEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

            // 1. Identity
            services.AddIdentity<User, IdentityRole>()
                .AddEntityFrameworkStores<ecommerceContext>() 
                .AddDefaultTokenProviders();

            // 2. Vault Services
            if (!isTestEnvironment)
            {
                services.AddCoreVaultServices(config, logger);
            }

            // 3. Database Contexts and Repositories
            services.AddDatabaseServices(config, isTestEnvironment);

            // 4. Redis, Data Protection, and Distributed Locking
            services.AddRedisAndDataProtection(config, logger).GetAwaiter().GetResult();

            // 5. Other Core Infrastructure Services
            services
                // .AddConfiguredMassTransit(config, isTestEnvironment, logger) // Pass logger if uncommented and needed
                .AddMinioStorage(config, logger) // Pass the logger instance
                .AddStripeServices(config, logger) // Pass the logger instance
                .AddSingleton<IVirusScanner, ClamAVScanner>() 
                .AddSingleton<IFileSignatureValidator, FileSignatureValidator>(); 

            return services;
        }

        #region Vault Services

        private static IServiceCollection AddCoreVaultServices(this IServiceCollection services, IConfiguration config, ILogger logger)
        {
            logger.LogInformation("[Vault] Registering core Vault services.");
            services.AddSingleton<IVaultService, VaultService>();
            services.AddSingleton<DynamicCredentialsConnectionInterceptor>();

            services.AddOptions<VaultOptions>()
                    .Bind(config.GetSection("VaultOptions"))
                    .ValidateDataAnnotations();

            services.AddHostedService<VaultCredentialRenewalService>();

            services.AddHealthChecks()
                .AddCheck<VaultHealthCheck>("vault_health_check", tags: new[] { "vault" })
                .AddCheck<DatabaseHealthCheck>("database_health_check", tags: new[] { "database" });
            
            return services;
        }

        #endregion

        #region Database Contexts and Repositories

        private static IServiceCollection AddDatabaseServices(this IServiceCollection services, IConfiguration config, bool isTestEnvironment)
        {
            services.AddDbContext<ecommerceContext>((sp, options) =>
            {
                var dbLogger = sp.GetRequiredService<ILogger<ecommerceContext>>(); 
                if (!isTestEnvironment)
                {
                    dbLogger.LogInformation("[DBContext-ecommerce] Configuring with Vault for non-test environment.");
                    var vault = sp.GetRequiredService<IVaultService>();
                    var connectionString = vault.GetDatabaseConnectionStringAsync().GetAwaiter().GetResult();
                    options.UseNpgsql(connectionString)
                           .AddInterceptors(sp.GetRequiredService<DynamicCredentialsConnectionInterceptor>());
                }
                else
                {
                    dbLogger.LogInformation("[DBContext-ecommerce] Configuring for test environment.");
                    var testConnectionString = config.GetConnectionString("ecommerceTestDatabase");
                    if (!string.IsNullOrEmpty(testConnectionString))
                    {
                        options.UseNpgsql(testConnectionString);
                    }
                    
                }
            });
            
            services.AddScoped<IProductRepository, ProductRepository>();
            services.AddScoped<IContentRepository, ContentRepository>();
            services.AddScoped<IOrderRepository, OrderRepository>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IPaymentRepository, PaymentRepository>();
            
            return services;
        }

        #endregion

        #region Redis, Data Protection, and Distributed Locking

        private static async Task<IServiceCollection> AddRedisAndDataProtection(
            this IServiceCollection services,
            IConfiguration config,
            ILogger logger) 
        {
            logger.LogInformation("[Redis] Starting Redis and Data Protection configuration...");

            var connectionString = config.GetConnectionString("Redis");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                logger.LogCritical("[Redis] Redis connection string is missing in configuration. Service setup will fail.");
                throw new ArgumentNullException(nameof(connectionString), "The Redis connection string ('Redis') is missing from the configuration.");
            }

            logger.LogInformation("[Redis] Using connection string (Masked if sensitive): {ConnectionString}", connectionString.Contains("password", StringComparison.OrdinalIgnoreCase) ? "******" : connectionString);

            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            logger.LogDebug("[Redis] Parsed configuration options: {@Options}", options);

            if (options.Ssl)
            {
                logger.LogInformation("[Redis] SSL enabled. Configuring certificates if paths are provided.");
                var certificatePath = config["Redis:CertificatePath"];
                var privateKeyPath = config["Redis:PrivateKeyPath"];

                if (!string.IsNullOrWhiteSpace(certificatePath) && !string.IsNullOrWhiteSpace(privateKeyPath))
                {
                    logger.LogInformation("[Redis] Using certificate: {CertificatePath} with key: {PrivateKeyPath}", certificatePath, privateKeyPath);
                    options.CertificateSelection += (_, _, _, _, _) =>
                    {
                        logger.LogDebug("[Redis] Creating X509Certificate2 from PEM files for SSL.");
                        return X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
                    };
                }
                else if (!string.IsNullOrWhiteSpace(certificatePath) || !string.IsNullOrWhiteSpace(privateKeyPath))
                {
                     logger.LogWarning("[Redis] SSL is enabled, but one of the certificate/key paths is missing. CertPath: '{CertPath}', KeyPath: '{KeyPath}'. SSL might not work as expected.", certificatePath, privateKeyPath);
                }
                else
                {
                    logger.LogInformation("[Redis] SSL is enabled, but no explicit certificate paths (Redis:CertificatePath, Redis:PrivateKeyPath) provided. Relying on system store or other mechanisms if needed.");
                }
            }

            logger.LogInformation("[Redis] Attempting to connect to Redis...");
            try
            {
                var redis = await ConnectionMultiplexer.ConnectAsync(options);
                logger.LogInformation("[Redis] Successfully connected to Redis. Configuration: {Configuration}", redis.Configuration);

                services.AddSingleton<IConnectionMultiplexer>(redis);
                services.AddStackExchangeRedisCache(o =>
                {
                    o.Configuration = redis.Configuration; 
                });

                services.AddDataProtection()
                        .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys");

                services.AddSingleton<IDistributedLockProvider, RedisDistributedLockProvider>(); 
                services.AddSingleton<IDistributedLockFactory>(sp =>
                {
                    logger.LogDebug("[Redis] Creating RedLock distributed lock factory.");
                    var multiplexers = new List<RedLockMultiplexer>
                    {
                        new RedLockMultiplexer(sp.GetRequiredService<IConnectionMultiplexer>())
                    };
                    return RedLockFactory.Create(multiplexers);
                });

                logger.LogInformation("[Redis] Redis and Data Protection services registered successfully.");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "[Redis] Failed to connect to Redis or register related services. Endpoints: {Endpoints}", string.Join(", ", options.EndPoints.Select(ep => ep.ToString())));
                throw new InvalidOperationException("Redis connection and service setup failed.", ex);
            }

            return services;
        }

        #endregion

        #region MassTransit, Minio, and Stripe

        private static IServiceCollection AddConfiguredMassTransit(this IServiceCollection services, IConfiguration config, bool isTestEnvironment, ILogger logger)
        {
            // Added logger parameter for potential logging within this method
            services.AddMassTransit(x =>
            {
                x.SetKebabCaseEndpointNameFormatter();
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    var rmq = config.GetSection("MassTransit:RabbitMq");
                    var host = rmq["Host"];
                    var username = rmq["Username"]; 
                    var password = rmq["Password"]; 

                    cfg.Host(host, h =>
                    {
                        h.Username(username ?? string.Empty);
                        h.Password(password ?? string.Empty);
                        if (rmq.GetValue<bool>("Ssl:Enabled"))
                        {
                            h.UseSsl(s =>
                            {
                                s.Protocol = SslProtocols.Tls12; 
                                s.ServerName = rmq["Ssl:ServerName"];
                            });
                        }
                    });
                    cfg.ConfigureEndpoints(ctx); 
                });
            });
            return services;
        }

        // Modified to accept and use the passed-in logger
        private static IServiceCollection AddMinioStorage(this IServiceCollection services, IConfiguration config, ILogger logger)
        {
            var minioConfig = config.GetSection("MinIO");
            var endpoint = minioConfig["Endpoint"];
            var accessKey = minioConfig["AccessKey"]; 
            var secretKey = minioConfig["SecretKey"]; 
            var useSsl = minioConfig.GetValue<bool>("Secure");

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                // Use the passed-in logger directly
                logger.LogCritical("[MinIO] MinIO configuration (Endpoint, AccessKey, or SecretKey) is missing. Storage service will not be functional.");
                throw new InvalidOperationException("MinIO endpoint, access key, or secret key is missing from configuration.");
            }
            
            services.AddSingleton<IMinioClient>(_ =>
                new MinioClient()
                    .WithEndpoint(endpoint)
                    .WithCredentials(accessKey, secretKey)
                    .WithSSL(useSsl)
                    .Build());
            
            services.AddSingleton<IContentStorageService, ContentStorageService>();
            
            return services;
        }

        // Modified to accept and use the passed-in logger
        private static IServiceCollection AddStripeServices(this IServiceCollection services, IConfiguration config, ILogger logger)
        {
            var stripeSecretKey = config["Stripe:SecretKey"]; 
            if (string.IsNullOrWhiteSpace(stripeSecretKey))
            {
                // Use the passed-in logger directly
                logger.LogCritical("[Stripe] Stripe SecretKey is missing. Stripe services will not be functional.");
                throw new InvalidOperationException("Stripe SecretKey is missing from configuration.");
            }
            StripeConfiguration.ApiKey = stripeSecretKey;

            services
                .AddSingleton<PaymentIntentService>() 
                .AddSingleton<SessionService>()      
                .AddScoped<StripeWebhookService>();  

            return services;
        }

        #endregion
    }
}