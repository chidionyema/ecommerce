using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace ecommerce.Middleware
{
    /// <summary>
    /// Middleware for centralized exception handling
    /// </summary>
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            try
            {
                await _next(httpContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during request processing");
                await HandleExceptionAsync(httpContext, ex);
            }
        }

        private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            // Determine if we should include detailed error information
            var isLocalEnvironment = context.Request.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || 
                                    context.Request.Host.Host.Equals("127.0.0.1");

            var response = new
            {
                status = context.Response.StatusCode,
                message = "An unexpected error occurred",
                // Only include exception details in local environments
                detail = isLocalEnvironment ? exception.Message : null,
                traceId = context.TraceIdentifier
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
        }
    }
}