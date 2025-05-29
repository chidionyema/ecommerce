using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ecommerce.Tests
{
    /// <summary>
    /// Middleware that injects authentication information into the request pipeline for testing.
    /// </summary>
    public class TestAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TestAuthMiddleware> _logger;

        public TestAuthMiddleware(RequestDelegate next, ILogger<TestAuthMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Only apply to API paths that need authentication
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                // Create test identity with required claims for ContentUploader role
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "test_auth_user"),
                    new Claim(ClaimTypes.Role, "ContentUploader"),
                    new Claim("permission", "upload_content"),
                };

                var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                // Set the user on the HttpContext
                context.User = principal;
                
                _logger.LogDebug("TestAuthMiddleware: Added test claims to request");
            }

            if (context.Request.Path.StartsWithSegments("/api/external-authentication/callback"))
            {
                // Get the login info from our store
                var loginInfo = ExternalLoginInfoStore.GetLoginInfo();
                
                if (loginInfo != null)
                {
                    _logger.LogInformation("TestAuthMiddleware found external login info for provider: {Provider}", 
                        loginInfo.LoginProvider);
                    
                    // Make the external login info accessible to SignInManager
                    context.Items["ExternalLoginInfo"] = loginInfo;
                }
                else
                {
                    _logger.LogWarning("No stored external login info found in TestAuthMiddleware");
                }
            }

            // Continue with the pipeline
            await _next(context);
        }

        
    }

    

    /// <summary>
    /// Extension methods for TestAuthMiddleware
    /// </summary>
    public static class TestAuthMiddlewareExtensions
    {
        /// <summary>
        /// Use test authentication middleware in the request pipeline
        /// </summary>
        public static IApplicationBuilder UseTestAuth(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<TestAuthMiddleware>();
        }

        /// <summary>
        /// Creates a WebApplicationFactory with the test auth middleware applied
        /// </summary>
        public static WebApplicationFactory<Program> WithTestAuth(this WebApplicationFactory<Program> factory)
        {
            return factory.WithWebHostBuilder(builder =>
            {
                // Register controllers explicitly
                builder.ConfigureServices(services =>
                {
                    services.AddControllers()
                        .AddApplicationPart(typeof(ecommerce.Controllers.ContentController).Assembly);
                });

                // Configure middleware pipeline
                builder.Configure(app =>
                {
                    // Ensure core middleware is configured
                    app.UseRouting();
                    
                    // Add TestAuth middleware
                    app.UseTestAuth();
                    
                    app.UseAuthentication();
                    app.UseAuthorization();
                    
                    // Map controllers
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                    });
                    
                    // Print routes for debugging
                    PrintRegisteredRoutes(app);
                });
            });
        }

        private static void PrintRegisteredRoutes(IApplicationBuilder app)
        {
            try
            {
                var endpointDataSource = app.ApplicationServices.GetService(typeof(EndpointDataSource)) as EndpointDataSource;
                if (endpointDataSource == null) return;
                
                var endpoints = endpointDataSource.Endpoints
                    .OfType<RouteEndpoint>()
                    .OrderBy(e => e.RoutePattern.RawText);
                
                Console.WriteLine("=== Registered Routes ===");
                foreach (var endpoint in endpoints)
                {
                    Console.WriteLine($"- {endpoint.RoutePattern.RawText}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error printing routes: {ex.Message}");
            }
        }
    }
}