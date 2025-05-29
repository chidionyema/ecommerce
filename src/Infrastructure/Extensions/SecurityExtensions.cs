using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using ecommerce.Models;
using ecommerce.Db;
using ecommerce.Services;
namespace ecommerce.Extensions
{
    /// <summary>
    /// Extension methods for configuring security-related services and middleware
    /// </summary>
    public static class SecurityExtensions
    {
        /// <summary>
        /// Registers all security services including authentication, authorization, and content security
        /// </summary>
        public static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
        {
            return services
                .AddAuthenticationSchemes(configuration)
                .AddExternalAuthenticationProviders(configuration)
                .AddAuthorizationPolicies()
                .AddRateLimiting(configuration)
                .AddContentSecurity(configuration);
        }

        /// <summary>
        /// Registers HTTP security services for header hardening (antiforgery, CORS, HSTS, HTTPS redirection).
        /// </summary>
        public static IServiceCollection AddContentSecurity(this IServiceCollection services, IConfiguration config)
        {
            // Configure antiforgery (CSRF protection)
            services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-CSRF-TOKEN";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
            });

            // Configure CORS for upload operations
            services.AddCors(options =>
            {
                options.AddPolicy("UploadPolicy", policy =>
                {
                    var origins = config["AllowedOrigins"]?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                ?? Array.Empty<string>();
                    policy.WithOrigins(origins)
                          .WithMethods("POST", "PUT", "DELETE")
                          .WithHeaders("Content-Type", "Authorization")
                          .WithExposedHeaders("Content-Disposition");
                });

                // Add specific CORS policy for local development
                options.AddPolicy("Allow3001",
                    policy => policy.WithOrigins("http://localhost:3001")
                                    .AllowAnyHeader()
                                    .AllowAnyMethod());
            });

            // Configure HSTS
            services.AddHsts(options =>
            {
                options.Preload = true;
                options.IncludeSubDomains = true;
                options.MaxAge = TimeSpan.FromDays(365);
            });

            // Configure HTTPS redirection (conditionally applied in middleware)
            services.AddHttpsRedirection(options =>
            {
                options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
                options.HttpsPort = 443;
            });

            return services;
        }

        /// <summary>
        /// Adds authentication schemes to the service collection
        /// </summary>
        private static IServiceCollection AddAuthenticationSchemes(this IServiceCollection services, IConfiguration configuration)
        {
            // Get JWT configuration
            var jwtSection = configuration.GetSection("Jwt");
            var keyMaterial = jwtSection["Key"] ?? configuration.GetSection("JwtSettings")["SecretKey"];
            var issuer = jwtSection["Issuer"] ?? configuration.GetSection("JwtSettings")["Issuer"];
            var audience = jwtSection["Audience"] ?? configuration.GetSection("JwtSettings")["Audience"];

            if (string.IsNullOrEmpty(keyMaterial))
            {
                throw new InvalidOperationException("JWT Secret Key is not configured");
            }

            var keyBytes = Convert.FromBase64String(keyMaterial);

            // Create token validation parameters
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                NameClaimType = ClaimTypes.NameIdentifier,
                ClockSkew = TimeSpan.Zero
            };

            // Share the same token parameters
            services.AddSingleton(tokenValidationParameters);

            // Check if we're in test environment
            var isTestEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

            // Configure authentication defaults
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                
                // Only set DefaultSignInScheme if not in test environment
                if (!isTestEnvironment)
                {
                    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                }
            })
            .AddPolicyScheme("MultiAuth", "MultiAuth", policyOpts =>
            {
                policyOpts.ForwardDefaultSelector = context =>
                {
                    // Check for JWT cookie first
                    if (context.Request.Cookies.ContainsKey("jwt"))
                    {
                        return "CookieAuth";
                    }

                    // Then check for Authorization header
                    if (context.Request.Headers.ContainsKey("Authorization"))
                    {
                        var authHeader = context.Request.Headers["Authorization"].ToString();
                        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            return JwtBearerDefaults.AuthenticationScheme;
                        }
                    }

                    return JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.SaveToken = true;
                options.RequireHttpsMetadata = !isTestEnvironment;
                options.TokenValidationParameters = tokenValidationParameters;

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // Log authentication failures
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                        logger.LogError(context.Exception, "JWT authentication failed");
                        return Task.CompletedTask;
                    },
                   OnTokenValidated = async context =>
                {
                    var authService = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                    var loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger("JwtBearerEvents"); // Or specific category

                    // The Principal is populated by the JwtBearerHandler after successful initial validation
                    var principal = context.Principal;

                    if (principal?.Identity?.IsAuthenticated ?? false)
                    {
                        // Extract JTI directly from the ClaimsPrincipal
                        var jtiClaim = principal.FindFirst(JwtRegisteredClaimNames.Jti);
                        var jti = jtiClaim?.Value;

                        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier); // Or JwtRegisteredClaimNames.Sub
                        var userId = userIdClaim?.Value;

                        logger.LogInformation("[JTI_TRACE_OnValidated] OnTokenValidated event triggered. UserID from claims: {UserId}, JTI from claims: {JTI}", userId ?? "N/A", jti ?? "N/A");

                        if (string.IsNullOrEmpty(jti))
                        {
                            logger.LogWarning("OnTokenValidated: Token for User ID: {UserId} is missing JTI claim. Revocation check cannot be performed based on JTI.", userId ?? "Unknown");
                            // Depending on your policy, you might want to fail tokens that are missing a JTI if revocation is critical.
                            // For example:
                            // context.Fail("Token is missing a JTI and cannot be checked for revocation.");
                            // return;
                        }
                        else if (await authService.IsTokenRevoked(jti)) // Calls your existing DB check
                        {
                            logger.LogWarning("OnTokenValidated: Token for User ID: {UserId}, JTI: {JTI} has been REVOKED. Failing authentication.", userId ?? "Unknown", jti);
                            context.Fail("This token has been revoked."); // This will cause authentication to fail
                            return;
                        }
                        else
                        {
                            logger.LogInformation("OnTokenValidated: Token for User ID: {UserId}, JTI: {JTI} is valid and not revoked.", userId ?? "Unknown", jti);
                        }
                    }
                    else
                    {
                        logger.LogWarning("OnTokenValidated: context.Principal or context.Principal.Identity is null or not authenticated. This is unexpected if the token was initially validated by the handler.");
                        // This case should ideally not be hit if the handler validated the token before this event.
                    }
                    // If you reach here, token is considered valid by this event handler's revocation check.
                    // The default lifetime checks (exp, nbf) in TokenValidationParameters also need to have passed for OnTokenValidated to be called with a valid Principal.
                },
                    OnMessageReceived = context =>
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                        
                        if (context.Token != null)
                        {
                            logger.LogInformation("JWT token already set: {TokenLength} chars", context.Token.Length);
                            return Task.CompletedTask;
                        }

                        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                        {
                            var authValue = authHeader.ToString();
                            if (authValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = authValue.Substring("Bearer ".Length).Trim();
                                logger.LogInformation("Extracted token from Authorization header: {TokenLength} chars",
                                    context.Token.Length);
                            }
                        }

                        if (context.Token == null && context.Request.Cookies.ContainsKey("jwt"))
                        {
                            context.Token = context.Request.Cookies["jwt"];
                            logger.LogInformation("Extracted token from cookie: {TokenLength} chars", context.Token.Length);
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<CookieAuthenticationOptions, JwtInCookieHandler>("CookieAuth", options =>
            {
                options.Cookie.Name = "jwt"; // Match the cookie name
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;

                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                };
            });

            // Only add the external scheme if we're not in test environment
            if (!isTestEnvironment)
            {
                // Check if the scheme is already registered (safer approach)
                var schemeProvider = services.BuildServiceProvider().GetService<IAuthenticationSchemeProvider>();
                var externalScheme = schemeProvider?.GetSchemeAsync(IdentityConstants.ExternalScheme).GetAwaiter().GetResult();
                
                if (externalScheme == null)
                {
                    services.AddAuthentication()
                        .AddCookie(IdentityConstants.ExternalScheme, options => 
                        {
                            options.Cookie.HttpOnly = true;
                            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                            options.Cookie.SameSite = SameSiteMode.Lax;
                            options.LoginPath = "/api/external-authentication/challenge";
                            
                            options.Events = new CookieAuthenticationEvents
                            {
                                OnRedirectToLogin = ctx => 
                                {
                                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                    return Task.CompletedTask;
                                },
                                OnRedirectToAccessDenied = ctx =>
                                {
                                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                                    return Task.CompletedTask;
                                }
                            };
                        });
                }
            }

            return services;
        }

        /// <summary>
        /// Adds external authentication providers (Google, Microsoft, Facebook)
        /// </summary>
        private static IServiceCollection AddExternalAuthenticationProviders(this IServiceCollection services, IConfiguration config)
        {
            

            services.AddAuthentication()
                .AddGoogle(options =>
                {
                    var googleAuthSection = config.GetSection("Authentication:Google");
                    options.ClientId = googleAuthSection["ClientId"] ?? throw new InvalidOperationException("Google ClientId missing!");
                    options.ClientSecret = googleAuthSection["ClientSecret"] ?? throw new InvalidOperationException("Google ClientSecret missing!");
                    options.CallbackPath = new PathString("/api/external-authentication/google-callback");
                    
                    // Optional: Configure user claims
                    options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                    options.ClaimActions.MapJsonKey("picture", "picture");
                    
                    // Save tokens for potential API access
                    options.SaveTokens = true;
                })
                .AddMicrosoftAccount(options =>
                {
                    var msAuthSection = config.GetSection("Authentication:Microsoft");
                    options.ClientId = msAuthSection["ClientId"] ?? throw new InvalidOperationException("Microsoft ClientId missing!");
                    options.ClientSecret = msAuthSection["ClientSecret"] ?? throw new InvalidOperationException("Microsoft ClientSecret missing!");
                    options.CallbackPath = new PathString("/api/external-authentication/microsoft-callback");
                    
                    // Save tokens
                    options.SaveTokens = true;
                })
                .AddFacebook(options =>
                {
                    var fbAuthSection = config.GetSection("Authentication:Facebook");
                    options.AppId = fbAuthSection["AppId"] ?? throw new InvalidOperationException("Facebook AppId missing!");
                    options.AppSecret = fbAuthSection["AppSecret"] ?? throw new InvalidOperationException("Facebook AppSecret missing!");
                    options.CallbackPath = new PathString("/api/external-authentication/facebook-callback");
                    
                    // Save tokens
                    options.SaveTokens = true;
                });

            return services;
        }

        /// <summary>
        /// Adds authorization policies to the service collection
        /// </summary>
        private static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
        {
            services.AddAuthorization(options =>
            {
                // Administrator role policy
                options.AddPolicy("AdminOnly", policy =>
                    policy.RequireClaim(ClaimTypes.Role, "Admin"));
                
                // Regular user role policy
                options.AddPolicy("RequireUserRole", policy => 
                    policy.RequireRole("User"));
                
                // Content uploader policy
                options.AddPolicy("ContentUploader", policy =>
                {
                    policy.RequireAuthenticatedUser()
                          .RequireRole("ContentUploader")
                          .RequireClaim("permission", "upload_content");
                });
            });

            return services;
        }

        /// <summary>
        /// Adds rate limiting to the service collection, but **skips** in Test environment
        /// </summary>
        private static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
        {
            // Check if we're in Test environment
            var isTestEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";
            if (isTestEnvironment)
            {
                // Do nothing - skip rate limiting in test mode
                return services;
            }

            var rateLimitSettings = configuration.GetSection("RateLimitSettings");
            var requestsPerMinute = rateLimitSettings.GetValue<int>("RequestsPerMinute");

            services.AddRateLimiter(options =>
            {
                // Global fixed window rate limiter
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetFixedWindowLimiter("Global", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerMinute > 0 ? requestsPerMinute : 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

                // API-specific rate limiter
                options.AddPolicy("api", httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter("api", _ => new FixedWindowRateLimiterOptions 
                    { 
                        PermitLimit = 30, 
                        Window = TimeSpan.FromSeconds(10) 
                    }));

                // Authentication endpoint limiter (prevent brute force)
                options.AddPolicy("auth", httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter("auth", _ => new FixedWindowRateLimiterOptions 
                    { 
                        PermitLimit = 10, 
                        Window = TimeSpan.FromMinutes(5) 
                    }));

                // Standard response for rejected requests
                options.OnRejected = async (context, cancellationToken) =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.ContentType = "application/json";
                    
                    await context.HttpContext.Response.WriteAsync(
                        "{\"error\":\"Too many requests. Please try again later.\"}",
                        cancellationToken
                    );
                };
            });

            return services;
        }

        /// <summary>
        /// Validates JWT key and sets up key rotation
        /// </summary>
        public static IApplicationBuilder ValidateJwtKeyAndSetupRotation(this IApplicationBuilder app)
        {
            var scopeFactory = app.ApplicationServices.GetRequiredService<IServiceScopeFactory>();
            using var scope = scopeFactory.CreateScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            var jwtSettings = configuration.GetSection("JwtSettings");
            var keyMaterial = jwtSettings["SecretKey"];
            
            if (string.IsNullOrEmpty(keyMaterial))
            {
                // Try alternative location
                jwtSettings = configuration.GetSection("Jwt");
                keyMaterial = jwtSettings["Key"];
            }

            if (string.IsNullOrEmpty(keyMaterial) || keyMaterial.Length < 32)
            {
                throw new InvalidOperationException("JWT Secret Key is not properly configured or is too weak");
            }

            // Setup key rotation logic here
            // This could involve registering a background service or checking key age

            return app;
        }

        /// <summary>
        /// Configures the middleware pipeline with security-focused middleware
        /// </summary>
        public static IApplicationBuilder ConfigureSecurityMiddleware(this IApplicationBuilder app, IConfiguration configuration)
        {
            // Add enhanced security headers (commented out in your code)
            // app.UseEnhancedSecurityHeaders(configuration);

            // Configure HSTS and HTTPS redirection
            app.UseHsts();
            app.UseHttpsRedirection();

            // Configure authentication and authorization
            app.UseAuthentication();
            app.UseAuthorization();

            // **Conditionally use rate limiter** - skip if it's Test environment
            var isTestEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";
            if (!isTestEnvironment)
            {
                app.UseRateLimiter();
            }

            return app;
        }

        /// <summary>
        /// Seeds the required roles during application startup
        /// </summary>
        public static async Task SeedRolesAsync(this IApplicationBuilder app)
        {
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

                // Check and create Admin role
                if (!await roleManager.RoleExistsAsync("Admin"))
                {
                    await roleManager.CreateAsync(new IdentityRole("Admin"));
                }

                // Check and create User role
                if (!await roleManager.RoleExistsAsync("User"))
                {
                    await roleManager.CreateAsync(new IdentityRole("User"));
                }

                // Check and create ContentUploader role
                if (!await roleManager.RoleExistsAsync("ContentUploader"))
                {
                    await roleManager.CreateAsync(new IdentityRole("ContentUploader"));
                }
            }
        }

        /// <summary>
        /// Custom authentication handler that validates JWT tokens stored in cookies
        /// </summary>
        internal class JwtInCookieHandler : CookieAuthenticationHandler
        {
            private readonly TokenValidationParameters _tokenValidationParams;
            private readonly ILogger<JwtInCookieHandler> _logger;

            public JwtInCookieHandler(
                IOptionsMonitor<CookieAuthenticationOptions> options,
                ILoggerFactory logger,
                UrlEncoder encoder,
                ISystemClock clock,
                IOptions<TokenValidationParameters> tokenValidationParams)
                : base(options, logger, encoder, clock)
            {
                _tokenValidationParams = tokenValidationParams.Value;
                _logger = logger.CreateLogger<JwtInCookieHandler>();
            }

            protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                try
                {
                    // Check if the cookie exists
                    if (!Request.Cookies.TryGetValue("jwt", out var jwtValue) || string.IsNullOrEmpty(jwtValue))
                    {
                        _logger.LogDebug("No jwt cookie found in the request");
                        return AuthenticateResult.NoResult();
                    }

                    _logger.LogDebug("Found jwt cookie with length: {Length}", jwtValue.Length);
                    var maskedJwt = jwtValue.Length > 8
                        ? $"{jwtValue.Substring(0, 4)}...{jwtValue.Substring(jwtValue.Length - 4)}"
                        : jwtValue;
                    _logger.LogDebug("Found jwt cookie with length: {Length} and token: {Token}", jwtValue.Length, maskedJwt);

                    // Validate the token
                    var handler = new JwtSecurityTokenHandler();
                    if (!handler.CanReadToken(jwtValue))
                    {
                        _logger.LogWarning("JWT cookie contains invalid token format");
                        return AuthenticateResult.Fail("Invalid token format");
                    }

                    // Use same token validation parameters as for the Bearer scheme
                    ClaimsPrincipal principal;
                    SecurityToken validatedToken;

                    try
                    {
                        principal = handler.ValidateToken(jwtValue, _tokenValidationParams, out validatedToken);
                    }
                    catch (SecurityTokenExpiredException ex)
                    {
                        _logger.LogInformation("JWT cookie expired: {Message}", ex.Message);
                        return AuthenticateResult.Fail("Token expired");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("JWT validation error: {Message}", ex.Message);
                        return AuthenticateResult.Fail("Invalid token");
                    }

                    // Check token expiration (belt and suspenders approach)
                    var jwtToken = validatedToken as JwtSecurityToken;
                    if (jwtToken == null || jwtToken.ValidTo < DateTime.UtcNow)
                    {
                        _logger.LogWarning("Token is expired: {Expiry}", jwtToken?.ValidTo);
                        return AuthenticateResult.Fail("Token expired");
                    }

                    _logger.LogInformation("JWT cookie validated successfully for user: {User}",
                        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");

                    // Create authentication ticket
                    var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), Scheme.Name);
                    return AuthenticateResult.Success(ticket);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "JWT cookie validation failed: {Message}", ex.Message);
                    return AuthenticateResult.Fail(ex);
                }
            }
        }
    }
}
