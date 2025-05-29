using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ecommerce.Services
{
    public class VaultHealthCheck : IHealthCheck
    {
        private readonly IVaultService _vaultService;

        public VaultHealthCheck(IVaultService vaultService)
        {
            _vaultService = vaultService;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var (_, password) = await _vaultService.GetDatabaseCredentialsAsync();
                if (password.Length == 0)
                    return HealthCheckResult.Unhealthy("Empty password");

                if (DateTime.UtcNow >= _vaultService.LeaseExpiry - TimeSpan.FromMinutes(10))
                    return HealthCheckResult.Degraded("Credentials nearing expiration");

                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Vault connection failure", ex);
            }
        }
    }
}
