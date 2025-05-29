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
    #region HttpClient Factories
    public static class HttpClientFactory
    {
                // In HttpClientFactory.cs
        public static HttpClient CreateWithCookies(CustomWebApplicationFactory factory, CookieContainer container)
        {
            var handler = new HttpClientHandler { 
                CookieContainer = container,
                UseCookies = true,
                AllowAutoRedirect = false  // Add this
            };
            
            // Create client with this handler
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
                BaseAddress = new Uri("http://localhost")
            });
            
            // Return client configured with this handler
            return client;
        }


        public static HttpClient CreateAuthorized(
            CustomWebApplicationFactory factory, string token)
        {
            return factory.WithWebHostBuilder(builder => 
                builder.ConfigureServices(services => 
                    services.PostConfigure<AuthorizationOptions>(options => 
                        options.AddPolicy(AuthorizationPolicies.ContentUploader, policy => 
                            policy.RequireAuthenticatedUser()
                                  .RequireRole(UserRoles.ContentUploader)))))
                .CreateClient()
                .WithBearerToken(token);
        }

        public static HttpClient CreateBypassAuth(CustomWebApplicationFactory factory)
        {
            return factory.WithWebHostBuilder(builder => 
                builder.ConfigureServices(services => 
                    services.AddSingleton<IAuthorizationHandler, AllowAllHandler>()))
                .CreateClient();
        }

        private static HttpClient WithBearerToken(this HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
    #endregion


}
