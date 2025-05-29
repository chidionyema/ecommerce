using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ecommerce.Db; // Add this using directive

namespace ecommerce.Services
{
    public class TokenCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TokenCleanupService> _logger;

        public TokenCleanupService(IServiceProvider serviceProvider, ILogger<TokenCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ecommerceContext>();

                    // Remove expired revoked tokens - using ExpiresAt instead of ExpiryDate
                    var expiredTokens = await context.RevokedTokens
                        .Where(t => t.ExpiresAt < DateTime.UtcNow)
                        .ToListAsync(stoppingToken);

                    if (expiredTokens.Any())
                    {
                        _logger.LogInformation("Removing {Count} expired revoked tokens", expiredTokens.Count);
                        context.RevokedTokens.RemoveRange(expiredTokens);
                        await context.SaveChangesAsync(stoppingToken);
                    }

                    // Also clean up expired refresh tokens
                    var expiredRefreshTokens = await context.RefreshTokens
                        .Where(t => t.Expires < DateTime.UtcNow)
                        .ToListAsync(stoppingToken);

                    if (expiredRefreshTokens.Any())
                    {
                        _logger.LogInformation("Removing {Count} expired refresh tokens", expiredRefreshTokens.Count);
                        context.RefreshTokens.RemoveRange(expiredRefreshTokens);
                        await context.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during token cleanup");
                }

                // Run once per day
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }
    }
}