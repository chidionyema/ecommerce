using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System;

namespace ecommerce.PolicyFactory
{
    public static class ResiliencePolicyFactory
    {
        public static IAsyncPolicy CreateVaultPolicy(
            IConfiguration config,
            ILogger logger)
        {
            // Example: read from config, or pick safe defaults if missing
            int maxRetries = config.GetValue<int>("Resilience:MaxRetries", 3);
            double initialDelayMs = config.GetValue<double>("Resilience:InitialDelayMs", 200);
            double circuitBreakerBreakSeconds = config.GetValue<double>("Resilience:CircuitBreakerBreakSeconds", 30);
            int circuitBreakerMinimumRequests = config.GetValue<int>("Resilience:CircuitBreakerMinimumRequests", 10);

            // 1) Retry policy: exponential backoff + logging
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    maxRetries,
                    retryAttempt =>
                        TimeSpan.FromMilliseconds(initialDelayMs * Math.Pow(2, retryAttempt - 1)),
                    (exception, timeSpan, retryCount, context) =>
                    {
                        logger.LogWarning(exception, $"Retry attempt {retryCount}");
                    });

            // 2) Circuit-breaker policy
            var circuitBreakerPolicy = Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: circuitBreakerMinimumRequests,
                    durationOfBreak: TimeSpan.FromSeconds(circuitBreakerBreakSeconds),
                    onBreak: (ex, breakDelay) =>
                    {
                        logger.LogCritical($"Circuit opened! Duration: {breakDelay.TotalSeconds} seconds");
                    },
                    onReset: () =>
                    {
                        logger.LogInformation("Circuit closed");
                    }
                );

            // Combine them: first retry, then circuit-break
            // => If retries still fail, the circuit-breaker triggers
            return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
        }
    }
}
