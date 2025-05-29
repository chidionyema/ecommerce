using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ecommerce.Services;
using ecommerce.Contracts;
using ecommerce.Models;

namespace ecommerce.Tests
{   
    public class FakeVirusScanner : IVirusScanner
    {
        public Task<VirusScanResult> ScanAsync(Stream fileStream)
        {
            // Fake virus scanning logic: Always return no virus detected for tests.
            return Task.FromResult(new VirusScanResult(false, "No Virus Detected"));
        }
    }

    public class FakeVaultService : IVaultService, IDisposable
    {
        // Required properties from the interface
        public TimeSpan LeaseDuration { get; private set; } = TimeSpan.FromSeconds(3600);
        public DateTime LeaseExpiry { get; private set; } = DateTime.UtcNow.AddSeconds(3600);
        
        private readonly ILogger<FakeVaultService> _logger;

        public FakeVaultService()
        {
            _logger = new LoggerFactory().CreateLogger<FakeVaultService>();
        }

        private static string CreateTempFile(string content)
        {
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, content);
            return tempFile;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            // No actual initialization needed for the fake service
            return Task.CompletedTask;
        }

        public Task<string> GetDatabaseConnectionStringAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("User ID=myuser;Password=mypassword;Host=localhost;Port=5433;Database=test_db;SSL Mode=Disable;");
        }

        public Task<(string Username, SecureString Password)> GetDatabaseCredentialsAsync(CancellationToken cancellationToken = default)
        {
            // In this fake service, we simulate a valid lease of 3600 seconds.
            LeaseDuration = TimeSpan.FromSeconds(3600);
            LeaseExpiry = DateTime.UtcNow.Add(LeaseDuration);

            var securePwd = new SecureString();
            foreach (var c in "mypassword")
            {
                securePwd.AppendChar(c);
            }
            securePwd.MakeReadOnly();

            return Task.FromResult(("myuser", securePwd));
        }

        public Task StartCredentialRenewalAsync(CancellationToken cancellationToken = default)
        {
            // No actual renewal needed for the fake service
            return Task.CompletedTask;
        }

        // Implement RefreshCredentials as a regular method (not override)
        public Task RefreshCredentials(CancellationToken cancellationToken = default)
        {
            // Simply update credentials with a safe lease.
            LeaseDuration = TimeSpan.FromSeconds(3600);
            LeaseExpiry = DateTime.UtcNow.Add(LeaseDuration);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // Nothing to dispose in this fake implementation
            GC.SuppressFinalize(this);
        }
    }
}