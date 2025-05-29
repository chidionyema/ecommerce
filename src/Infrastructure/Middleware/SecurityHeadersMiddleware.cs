using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace ecommerce.Infrastructure.Middleware
{
     public static class SecurityHeadersMiddleware
    {
        public static IApplicationBuilder UseEnhancedSecurityHeaders(this IApplicationBuilder app, IConfiguration config)
        {
            return app.Use(async (context, next) =>
            {
                // Content Security Policy - Prevent XSS
                context.Response.Headers.Append("Content-Security-Policy", 
                    "default-src 'self'; script-src 'self' https://trusted.cdn.com; style-src 'self' https://trusted.styles.com; img-src 'self' data:; font-src 'self'; object-src 'none'; frame-ancestors 'none';");
                
                // Strict Transport Security - Force HTTPS
                context.Response.Headers.Append("Strict-Transport-Security", 
                    "max-age=31536000; includeSubDomains; preload");
                
                // Protect against clickjacking
                context.Response.Headers.Append("X-Frame-Options", "DENY");
                
                // Block MIME type sniffing
                context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
                
                // Cross-site scripting filter
                context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
                
                // Referrer Policy
                context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
                
                // Permissions Policy (formerly Feature Policy)
                context.Response.Headers.Append("Permissions-Policy", 
                    "camera=(), microphone=(), geolocation=(), payment=()");
                
                await next();
            });
        }
    }
}