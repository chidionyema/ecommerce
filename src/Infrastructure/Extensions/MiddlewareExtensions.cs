using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection; // Add this line
using System;
using System.Threading.Tasks;
using ecommerce.Services;
using ecommerce.Middleware;
using Swashbuckle.AspNetCore.SwaggerUI;
using ecommerce.Infrastructure.Middleware;

namespace ecommerce.Extensions
{
    public static class MiddlewareExtensions
    {
        /// <summary>
        /// Configures the complete middleware pipeline for the application.
        /// Rate limiting is only enabled in non-test environments.
        /// </summary>
        public static void ConfigureMiddlewarePipeline(this WebApplication app)
        {
            // Apply enhanced security headers and error handling first.
            app.UseEnhancedSecurityHeaders(app.Configuration)
               .UseExceptionHandler("/error");

            // Always enforce HTTPS in production environments (but not in Test).
            if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            // Serve static files and enable routing.
            app.UseStaticFiles()
               .UseRouting();

            // Configure CORS before authentication.
            app.UseCors("Allow3001"); // Adjust CORS policy as needed.

            // Apply authentication and authorization middleware.
            app.UseAuthentication()
               .UseAuthorization();

            // Only apply rate limiting if NOT in the "Test" environment.
            if (!app.Environment.IsEnvironment("Test"))
            {
                app.UseRateLimiter();
            }

            // Custom exception handling middleware.
            app.UseMiddleware<ExceptionHandlingMiddleware>();

            // Enable Swagger in development or staging environments.
            if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
            {
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
                    
                    // If not in development, optionally protect Swagger UI.
                    if (!app.Environment.IsDevelopment())
                    {
                        // Inline basic authentication (or your custom logic) for Swagger UI.
                        string username = "swagger";
                        string password = app.Configuration["Swagger:Password"] ?? "securePassword";
                        c.HeadContent += @$"
                            <script>
                                window.onload = function() {{
                                    const expectedUsername = '{username}';
                                    const expectedPassword = '{password}';
                                    if (!localStorage.getItem('swaggerBasicAuth')) {{
                                        const credentials = prompt('Enter Swagger credentials (format: username:password):', 'username:password');
                                        if (credentials) {{
                                            const [user, pass] = credentials.split(':');
                                            if (user === expectedUsername && pass === expectedPassword) {{
                                                localStorage.setItem('swaggerBasicAuth', 'true');
                                            }} else {{
                                                alert('Invalid credentials');
                                                window.location.reload();
                                            }}
                                        }} else {{
                                            window.location.href = '/';
                                        }}
                                    }}
                                }};
                            </script>";
                    }
                });
            }

            // Map controllers.
            app.MapControllers();
        }

        /// <summary>
        /// Initializes the vault service asynchronously.
        /// </summary>
        public static async Task InitializeVaultServiceAsync(this WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var vault = scope.ServiceProvider.GetRequiredService<IVaultService>();
            await vault.InitializeAsync();
        }
    }
}
