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

    #region Configuration Extensions
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection RemoveSwaggerGenerator(this IServiceCollection services)
        {
            services.RemoveAll<ISwaggerProvider>();
            return services;
        }
        

        public static IServiceCollection ConfigureTestAuthorization(this IServiceCollection services)
        {
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                options.AddPolicy(AuthorizationPolicies.ContentUploader, policy => 
                    policy.RequireRole(UserRoles.ContentUploader));
            });
            return services;
        }

        public static IServiceCollection ReplaceService<TService, TImplementation>(
        this IServiceCollection services, 
        Func<IServiceProvider, TImplementation> implementationFactory) 
        where TService : class
        where TImplementation : class, TService
    {
        services.RemoveAll<TService>();
        services.AddSingleton<TService, TImplementation>(implementationFactory); 
        return services;
    }

      public static IServiceCollection ConfigureContentStorage(
    this IServiceCollection services, IConfiguration config)
{
    // Add the MinioClient as a singleton first
    services.AddSingleton<IMinioClient>(sp => MinioTestClient.Get(config));
    
    // Then use the same client instance for the ContentStorageService
    return services.AddSingleton<IContentStorageService>(sp => 
        new ContentStorageService(
            sp.GetRequiredService<IMinioClient>(),
            sp.GetRequiredService<ILogger<ContentStorageService>>(),
            config));
}

        public static IServiceCollection ConfigureDatabases(
            this IServiceCollection services, IConfiguration config)
        {
            var connectionString = config.GetConnectionString("DefaultConnection");
            
            services.AddDbContext<ecommerceContext>(options => 
                options.ConfigureNpgsql(connectionString));
            
            //services.AddDbContext<ContentContext>(options => 
            //options.ConfigureNpgsql(connectionString));
            
            //services.AddDbContext<ProductContext>(options => 
              // options.ConfigureNpgsql(connectionString));

            return services;
        }

        public static IServiceCollection ConfigureRedis(
            this IServiceCollection services, IConfiguration config)
        {
            return services.AddSingleton<IConnectionMultiplexer>(sp => 
                ConnectionMultiplexer.Connect(
                    ConfigurationOptions.Parse(config.GetConnectionString("Redis"))));
        }

        public static void ConfigureNpgsql(
            this DbContextOptionsBuilder options, string connectionString)
        {
            options.UseNpgsql(connectionString)
                   .ConfigureWarnings(warnings => 
                       warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        }

        public static void ConfigureJwtBearer(
            this IServiceCollection services, IConfiguration config)
        {
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                options => ConfigureTokenValidation(options, config));
        }

        private static void ConfigureTokenValidation(
            Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options, 
            IConfiguration config)
        {
            options.SaveToken = true;
            options.IncludeErrorDetails = true;
            options.TokenValidationParameters = CreateValidationParameters(config);
        }

        private static TokenValidationParameters CreateValidationParameters(IConfiguration config)
        {
            var key = Convert.FromBase64String(config["Jwt:Key"]);
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = config["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = config["Jwt:Audience"],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier
            };
        }
    }
    #endregion



}
