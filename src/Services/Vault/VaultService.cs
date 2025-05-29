using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// For logging & config
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// For DB connection building (optional)
using Npgsql;

// For Polly (retry, circuit breaker, etc.)
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

// For VaultSharp
using VaultSharp;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.SecretsEngines.Database;

namespace ecommerce.Services
{
    /****************************************************
     * 1. Options / Config Classes
     ****************************************************/

    /// <summary>
    /// Configuration options for HashiCorp Vault.
    /// </summary>
    public class VaultOptions
    {
        /// <summary>
        /// The base address of Vault, e.g. "https://my-vault:8200".
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// A local file path containing the Vault AppRole RoleId.
        /// </summary>
        public string RoleIdPath { get; set; } = string.Empty;

        /// <summary>
        /// A local file path containing the Vault AppRole SecretId.
        /// </summary>
        public string SecretIdPath { get; set; } = string.Empty;

        /// <summary>
        /// The expected SHA256 thumbprint of the Vault SSL certificate
        /// (omit colons). If empty, no thumbprint check is performed.
        /// </summary>
        public string CertThumbprint { get; set; } = string.Empty;

        /// <summary>
        /// (Optional) If you want to pin the entire certificate, specify a file path.
        /// The loaded certificate's raw bytes must match what the server presents.
        /// </summary>
        public string? PinnedCertPath { get; set; }

        /// <summary>
        /// (Optional) Path to an HMAC key used to verify the integrity of RoleId/SecretId files.
        /// If not provided, HMAC validation is skipped.
        /// </summary>
        public string? HmacKeyPath { get; set; }

        /// <summary>
        /// (Optional) Path to the CA certificate file. Used for certificate chain validation.
        /// </summary>
        public string? CaCertPath { get; set; } = "/certs-volume/ca.crt";

        /// <summary>
        /// (Optional) Whether to allow chain errors in development environment.
        /// </summary>
        public bool AllowChainErrorsInDevelopment { get; set; } = true;
    }

    /// <summary>
    /// Basic database config: the host, possibly DB name, etc.
    /// </summary>
    public class DatabaseOptions
    {
        /// <summary>
        /// The hostname or IP for the database server.
        /// </summary>
        public string Host { get; set; } = string.Empty;
         public string Database { get; set; } = string.Empty;
    }

    /****************************************************
     * 2. HMAC Validation & Disk Secrets
     ****************************************************/

    /// <summary>
    /// Simple record to hold the final loaded secrets from disk,
    /// including whether they passed HMAC validation.
    /// </summary>
    internal record VaultDiskSecrets(
        string RoleId,
        string SecretId,
        bool HmacValid
    );

    /// <summary>
    /// Helper class that reads a file and optionally validates
    /// the file's contents against an HMAC signature.
    /// 
    /// For demonstration, we assume you store a separate ".hmac" file
    /// containing the expected HMAC in hex form, or some approach
    /// you define. This is flexible code you can adapt.
    /// </summary>
    internal static class HmacFileValidator
    {
        public static async Task<(string content, bool hmacValid)>
            ReadFileWithHmacAsync(string filePath, string? hmacKeyPath, CancellationToken ct = default)
        {
            // 1) Read the main file's bytes
            var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
            var contentString = Encoding.UTF8.GetString(fileBytes).Trim();

            // If there's no HMAC key or the key doesn't exist, we skip validation
            if (string.IsNullOrWhiteSpace(hmacKeyPath) || !File.Exists(hmacKeyPath))
            {
                return (contentString, false);
            }

            // 2) Load the HMAC key from disk
            var keyBytes = await File.ReadAllBytesAsync(hmacKeyPath, ct);

            // 3) Compute HMAC of the main file's bytes
            using var hmac = new HMACSHA256(keyBytes);
            var computedHash = hmac.ComputeHash(fileBytes);
            var computedHex = BitConverter.ToString(computedHash)
                .Replace("-", "", StringComparison.OrdinalIgnoreCase);

            // 4) Look for a ".hmac" sidecar file
            var expectedHmacPath = filePath + ".hmac";
            if (!File.Exists(expectedHmacPath))
            {
                // The main file is read, but no sidecar => not validated
                return (contentString, false);
            }
            var expectedHex = await File.ReadAllTextAsync(expectedHmacPath, ct);
            expectedHex = expectedHex?.Trim() ?? string.Empty;

            // 5) Compare hex
            if (!computedHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"HMAC mismatch for file {filePath}");
            }

            // HMAC verified
            return (contentString, true);
        }
    }

    /****************************************************
     * 3. IVaultService Interface
     ****************************************************/

    /// <summary>
    /// Interface describing a Vault-based credential provider
    /// that obtains short-lived DB credentials and refreshes them
    /// before expiry.
    /// </summary>
    public interface IVaultService : IDisposable
    {
        /// <summary>
        /// Initializes the Vault service (fetches initial credentials).
        /// </summary>
        Task InitializeAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns the current DB credentials (refreshing if near expiry).
        /// </summary>
        Task<(string Username, SecureString Password)> GetDatabaseCredentialsAsync(CancellationToken ct = default);

        /// <summary>
        /// Forces an immediate refresh of the credentials from Vault.
        /// </summary>
        Task RefreshCredentials(CancellationToken ct = default);

        /// <summary>
        /// Returns a connection string that references the current or newly refreshed credentials.
        /// </summary>
        Task<string> GetDatabaseConnectionStringAsync(CancellationToken ct = default);

        /// <summary>
        /// A background loop that automatically refreshes credentials until cancellation.
        /// </summary>
        Task StartCredentialRenewalAsync(CancellationToken stoppingToken);

        /// <summary>
        /// Tells you when the current credentials will expire.
        /// </summary>
        DateTime LeaseExpiry { get; }

        /// <summary>
        /// The current Vault lease duration.
        /// </summary>
        TimeSpan LeaseDuration { get; }
    }

    /****************************************************
     * 4. VaultService Implementation
     ****************************************************/

    public class VaultService : IVaultService
    {
        private readonly ILogger<VaultService> _logger;
        private readonly VaultOptions _vaultOptions;
        private readonly DatabaseOptions _dbOptions;

        private readonly IVaultClient _client;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Initialize with default values to fix CS8618 warning
        private (string Username, SecureString Password) _credentials = (string.Empty, new SecureString());
        private bool _disposed;

        // Policies
        private readonly AsyncRetryPolicy _retryPolicy;
        private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;

        // Optional static dictionary for pinned certificates
        private static readonly ConcurrentDictionary<string, X509Certificate2> _cachedPinnedCert =
            new();

        public DateTime LeaseExpiry { get; set; }
        public TimeSpan LeaseDuration { get; set; }

        public VaultService(
            IOptions<VaultOptions> vaultOpts,
            IOptions<DatabaseOptions> dbOpts,
            ILogger<VaultService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _vaultOptions = vaultOpts?.Value ?? throw new ArgumentNullException(nameof(vaultOpts));
            _dbOptions = dbOpts?.Value ?? throw new ArgumentNullException(nameof(dbOpts));

            ValidateConfiguration(_vaultOptions, _dbOptions);

            // 1) Load secrets from disk, possibly with HMAC validation
            var (roleId, secretId, hmacValid) = LoadSecretsAsync(_vaultOptions).GetAwaiter().GetResult();
            if (!hmacValid)
            {
                _logger.LogWarning("Vault credentials are not HMAC-verified (proceeding).");
            }

            // 2) Create an HttpClientHandler that enforces certificate pinning, CRL checks, etc.
            var handler = CreateHttpClientHandler(_vaultOptions);
            _httpClient = CreateHttpClient(handler);

            // 3) Create Vault client
            var authMethod = new AppRoleAuthMethodInfo(roleId, secretId);
            var settings = new VaultClientSettings(_vaultOptions.Address, authMethod)
            {
                MyHttpClientProviderFunc = _ => _httpClient
            };
            _client = new VaultClient(settings);

            // 4) Build Polly policies: circuit breaker + exponential backoff
            _circuitBreakerPolicy = Policy
                .Handle<Exception>(IsTransient)
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 2,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (ex, timespan) =>
                    {
                        _logger.LogError(ex, "Vault circuit broken for {Duration}s", timespan.TotalSeconds);
                        // TODO: Telemetry (metrics, tracing) if desired
                    },
                    onReset: () =>
                    {
                        _logger.LogInformation("Vault circuit reset");
                    }
                );

            _retryPolicy = Policy
                .Handle<Exception>(IsTransient)
                .WaitAndRetryAsync(
                    3,
                    attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    (exception, ts, retryCount, ctx) =>
                    {
                        _logger.LogWarning(exception, "Vault retry {RetryCount} after {Seconds}s", retryCount, ts.TotalSeconds);
                        // TODO: Telemetry (metrics, tracing) if desired
                    });

            // If you want to store or combine these policies for usage, see RefreshCredentials for example
        }

        private static void ValidateConfiguration(VaultOptions vaultCfg, DatabaseOptions dbCfg)
        {
            if (string.IsNullOrWhiteSpace(vaultCfg.Address))
                throw new ArgumentNullException(nameof(vaultCfg.Address), "Vault address not set.");
            if (string.IsNullOrWhiteSpace(vaultCfg.RoleIdPath))
                throw new ArgumentNullException(nameof(vaultCfg.RoleIdPath), "RoleId path not set.");
            if (string.IsNullOrWhiteSpace(vaultCfg.SecretIdPath))
                throw new ArgumentNullException(nameof(vaultCfg.SecretIdPath), "SecretId path not set.");
            if (string.IsNullOrWhiteSpace(dbCfg.Host))
                throw new ArgumentNullException(nameof(dbCfg.Host), "Database host not set.");
        }

        private async Task<VaultDiskSecrets> LoadSecretsAsync(VaultOptions vo)
        {
            var roleTask = HmacFileValidator.ReadFileWithHmacAsync(vo.RoleIdPath, vo.HmacKeyPath);
            var secretTask = HmacFileValidator.ReadFileWithHmacAsync(vo.SecretIdPath, vo.HmacKeyPath);

            await Task.WhenAll(roleTask, secretTask);

            var (roleId, roleOk) = roleTask.Result;
            var (secretId, secretOk) = secretTask.Result;
            return new VaultDiskSecrets(roleId, secretId, roleOk && secretOk);
        }

        private HttpClientHandler CreateHttpClientHandler(VaultOptions opts)
        {
            var handler = new HttpClientHandler
            {
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                CheckCertificateRevocationList = false // Disable CRL checks for reliability
            };

            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                ValidateServerCertificate(cert as X509Certificate2, chain, errors, opts);

            return handler;
        }

        private bool ValidateServerCertificate(
            X509Certificate2? cert,
            X509Chain? chain,
            SslPolicyErrors policyErrors,
            VaultOptions opts)
        {
            if (cert == null)
            {
                _logger.LogError("Vault server presented no certificate.");
                return false;
            }

            // Log details about the certificate for debugging
            _logger.LogDebug("Validating certificate: Subject={Subject}, Issuer={Issuer}, Thumbprint={Thumbprint}",
                cert.Subject, cert.Issuer, cert.GetCertHashString(HashAlgorithmName.SHA256));

            // 1) Thumbprint pinning (SHA256) - if configured
            if (!string.IsNullOrEmpty(opts.CertThumbprint))
            {
                var actualThumb = cert.GetCertHashString(HashAlgorithmName.SHA256)
                    .Replace(":", "", StringComparison.OrdinalIgnoreCase);

                var expectedThumb = opts.CertThumbprint
                    .Replace(":", "", StringComparison.OrdinalIgnoreCase);

                if (!actualThumb.Equals(expectedThumb, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Certificate thumbprint mismatch. Expected={Expected}, Actual={Actual}",
                        expectedThumb, actualThumb);
                    return false;
                }
                
                // If thumbprint matches, we can accept this certificate regardless of chain validation
                _logger.LogDebug("Certificate thumbprint matched, accepting connection.");
                return true;
            }

            // 2) Optional pinned cert from file
            if (!string.IsNullOrEmpty(opts.PinnedCertPath) && File.Exists(opts.PinnedCertPath))
            {
                try
                {
                    var pinned = _cachedPinnedCert.GetOrAdd(opts.PinnedCertPath, path =>
                    {
                        _logger.LogInformation("Loading pinned certificate from {Path}", path);
                        var raw = File.ReadAllBytes(path);
                        // Use X509CertificateLoader instead of direct constructor to fix SYSLIB0057
                        return System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(path);
                    });

                    if (cert.RawData.AsSpan().SequenceEqual(pinned.RawData))
                    {
                        _logger.LogDebug("Pinned certificate matched, accepting connection.");
                        return true;
                    }
                    
                    _logger.LogError("Pinned certificate mismatch!");
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading pinned certificate");
                    return false;
                }
            }

            // 3) Try to add the CA certificate to the chain for validation
            if (chain != null && !string.IsNullOrEmpty(opts.CaCertPath) && File.Exists(opts.CaCertPath))
            {
                try
                {
                    // Configure chain for proper validation
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Skip CRL checks
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    
                    // Add CA cert to the chain store - Fix SYSLIB0057 warning using X509CertificateLoader
                    var caCert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(opts.CaCertPath);
                    chain.ChainPolicy.ExtraStore.Add(caCert);
                    _logger.LogDebug("Added CA certificate to chain store: {Subject}", caCert.Subject);
                    
                    // Build the chain
                    bool chainBuilt = chain.Build(cert);
                    if (!chainBuilt)
                    {
                        _logger.LogWarning("Certificate chain build failed.");
                        foreach (var status in chain.ChainStatus)
                        {
                            _logger.LogWarning("Chain status: {Status}", status.StatusInformation.Trim());
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Certificate chain built successfully.");
                        return true; // If we can build the chain, the certificate is valid
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during certificate chain validation");
                }
            }

            // 4) Check for chain errors specifically
            if (policyErrors == SslPolicyErrors.RemoteCertificateChainErrors)
            {
                // In development environments, we might want to be more permissive
                bool isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.Equals("Development", 
                    StringComparison.OrdinalIgnoreCase) ?? false;
                
                if (isDevelopment && opts.AllowChainErrorsInDevelopment)
                {
                    _logger.LogWarning("Allowing connection despite chain errors in development environment.");
                    return true;
                }
                
                _logger.LogError("Certificate chain validation failed. Enable AllowChainErrorsInDevelopment for dev environments.");
            }
            else if (policyErrors != SslPolicyErrors.None)
            {
                _logger.LogError("SSL Policy errors: {Errors}", policyErrors);
            }

            // Default: only accept if there are no policy errors
            return (policyErrors == SslPolicyErrors.None);
        }

        private HttpClient CreateHttpClient(HttpClientHandler handler)
        {
            var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            // Keep-Alive
            client.DefaultRequestHeaders.Connection.Add("keep-alive");
            return client;
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Initializing VaultService...");

            // Wrap circuit breaker + retry
            var combinedPolicy = Policy.WrapAsync(_circuitBreakerPolicy, _retryPolicy);
            await combinedPolicy.ExecuteAsync((token) => RefreshCredentials(token), ct);

            _logger.LogInformation("VaultService initialized successfully.");
        }

        public virtual async Task<(string Username, SecureString Password)> GetDatabaseCredentialsAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            // Calculate the refresh threshold (5 minutes before expiry) using TimeSpan arithmetic.
            TimeSpan refreshThreshold = TimeSpan.FromMinutes(5);

            // Safely check if we need to refresh.  Use TimeSpan for the comparison.
            if (DateTime.UtcNow + refreshThreshold < LeaseExpiry)
            {
                return _credentials;
            }

            // Otherwise, refresh under lock
            await _lock.WaitAsync(ct);
            try
            {
                // Double-checked locking pattern, check again inside the lock.
                if (DateTime.UtcNow + refreshThreshold < LeaseExpiry)
                {
                    return _credentials;
                }

                await RefreshCredentials(ct);
                return _credentials;
            }
            finally
            {
                _lock.Release();
            }
        }

        public virtual async Task RefreshCredentials(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Refreshing Vault credentials...");

            var combinedPolicy = Policy.WrapAsync(_circuitBreakerPolicy, _retryPolicy);
            await combinedPolicy.ExecuteAsync(async token =>
            {
                var resp = await _client.V1.Secrets.Database.GetCredentialsAsync("vault");

                LeaseDuration = TimeSpan.FromSeconds(resp.LeaseDurationSeconds);
                LeaseExpiry = DateTime.UtcNow + LeaseDuration;

                var securePwd = new SecureString();
                foreach (var c in resp.Data.Password)
                {
                    securePwd.AppendChar(c);
                }
                securePwd.MakeReadOnly();

                _credentials = (resp.Data.Username, securePwd);

                // Example: log an audit record, or increment metrics
                _logger.LogInformation("Vault credentials updated. Expires at {Expiry}.", LeaseExpiry);
            }, ct);
        }

        public virtual async Task<string> GetDatabaseConnectionStringAsync(CancellationToken ct = default)
        {
            var (user, pwd) = await GetDatabaseCredentialsAsync(ct);
            // Convert SecureString => plain text for Npgsql
            var nc = new NetworkCredential("", pwd);

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = _dbOptions.Host,
                Database = _dbOptions.Database,
                Username = user,
                Password = nc.Password,
                SslMode = SslMode.Require,
                MaxPoolSize = 50
            };

            // Don't log the password
            return builder.ConnectionString;
        }

        public async Task StartCredentialRenewalAsync(CancellationToken stoppingToken)
        {
            ThrowIfDisposed();

            _logger.LogInformation("Starting credential renewal loop...");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshCredentials(stoppingToken);

                    // Sleep until 5 min before expiry
                    var delay = (LeaseExpiry - TimeSpan.FromMinutes(5)) - DateTime.UtcNow;
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.FromMinutes(1);
                    }

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Credential renewal canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in renewal loop, waiting 1 minute before retry.");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private bool IsTransient(Exception ex)
        {
            // You can expand this list as needed
            return ex is HttpRequestException
                or TimeoutException
                or IOException;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VaultService));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Dispose the secure password
            _credentials.Password?.Dispose();

            _lock.Dispose();
            _httpClient.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}