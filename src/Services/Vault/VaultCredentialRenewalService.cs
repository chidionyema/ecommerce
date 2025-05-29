using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.SecretsEngines.Database;
using Polly;

namespace ecommerce.Services
{
    public class VaultCredentialRenewalService : BackgroundService
    {
        private readonly IVaultService _vault;
        private readonly ILogger<VaultCredentialRenewalService> _logger;

        public VaultCredentialRenewalService(
            IVaultService vault,
            ILogger<VaultCredentialRenewalService> logger)
        {
            _vault = vault;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting credential renewal service");
            await _vault.StartCredentialRenewalAsync(stoppingToken);
        }
    }
}
