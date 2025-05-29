using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using Npgsql;
using ecommerce.Services;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Net;


namespace ecommerce.Db
{
    public class DynamicCredentialsConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly IVaultService _vault;
        private readonly ILogger<DynamicCredentialsConnectionInterceptor> _logger;

        public DynamicCredentialsConnectionInterceptor(
            IVaultService vault,
            ILogger<DynamicCredentialsConnectionInterceptor> logger)
        {
            _vault = vault;
            _logger = logger;
        }

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (connection is NpgsqlConnection npgConn)
            {
                try
                {
                    var (username, password) = await _vault.GetDatabaseCredentialsAsync();
                    var insecurePassword = new NetworkCredential(string.Empty, password).Password;
                    
                    var builder = new NpgsqlConnectionStringBuilder(npgConn.ConnectionString)
                    {
                        Username = username,
                        Password = insecurePassword
                    };

                    npgConn.ConnectionString = builder.ToString();
                    _logger.LogDebug("Updated connection credentials");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh database credentials");
                    throw;
                }
            }
            return await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        }
    }
}