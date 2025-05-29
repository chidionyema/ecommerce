using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.SecretsEngines.Database;
using VaultSharp.V1.SecretsEngines.Database.Models; // Using the VaultSharp model
using Polly;
/*
namespace ecommerce.Services
{
     public interface IVaultService
    {
        Task<string> GetDatabaseConnectionString();
        Task StartCredentialRenewal(CancellationToken cancellationToken);
    }
    public class VaultService : IVaultService
    {
        private readonly IVaultClient _client;
        private VaultSharp.V1.SecretsEngines.UsernamePasswordCredentials _currentCredentials;
        private DateTime _leaseObtainedTime;
        private int _leaseDurationSeconds; // Track lease duration separately

        public VaultService(IConfiguration config)
        {
            var roleId = File.ReadAllText(config["Vault:RoleIdPath"]);
            var secretId = File.ReadAllText(config["Vault:SecretIdPath"]);

            var authMethod = new AppRoleAuthMethodInfo(roleId, secretId);
            var vaultSettings = new VaultClientSettings(config["Vault:Address"], authMethod);
            _client = new VaultClient(vaultSettings);
        }

        public virtual async Task<string> GetDatabaseConnectionString()
        {
            // Fetch new credentials if none exist or if they are about to expire
            if (_currentCredentials == null || IsLeaseAboutToExpire())
            {
                var response = await _client.V1.Secrets.Database.GetCredentialsAsync("vault");
                _currentCredentials = response.Data;
                // Instead of trying to use a non-existent LeaseDuration property,
                // assign a default lease duration (e.g., 3600 seconds)
                _leaseDurationSeconds = 3600;
                _leaseObtainedTime = DateTime.UtcNow;
            }

            return $"User ID={_currentCredentials.Username};Password={_currentCredentials.Password};" +
                   "Host=postgres_primary;Port=5432;Database=your_postgres_db;SSL Mode=Require;Trust Server Certificate=true;";
        }

        private bool IsLeaseAboutToExpire()
        {
            if (_currentCredentials == null)
                return true;

            var elapsed = DateTime.UtcNow - _leaseObtainedTime;
            return elapsed.TotalSeconds >= _leaseDurationSeconds * 0.75;
        }

        public async Task StartCredentialRenewal(CancellationToken cancellationToken)
        {
            // Initial credential fetch
            await GetDatabaseConnectionString();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var elapsed = DateTime.UtcNow - _leaseObtainedTime;
                    var remaining = TimeSpan.FromSeconds(_leaseDurationSeconds) - elapsed;

                    var renewalTime = remaining.TotalSeconds > 0 
                        ? TimeSpan.FromSeconds(remaining.TotalSeconds * 0.75) 
                        : TimeSpan.Zero;

                    await Task.Delay(renewalTime, cancellationToken);
                    await GetDatabaseConnectionString();
                }
                catch (Exception ex)
                {
                    // Optionally log the exception here
                    await Policy
                        .Handle<Exception>()
                        .WaitAndRetryForeverAsync(_ => TimeSpan.FromSeconds(10))
                        .ExecuteAsync(() => GetDatabaseConnectionString());
                }
            }
        }
    }
}
*/