
using Npgsql;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ecommerce.Db;
using ecommerce.Services;
using System;
using System.Linq;



namespace ecommerce.Db
{
    public abstract class BaseDbContext : DbContext
    {
        protected readonly IConfiguration _configuration;
        protected readonly ILoggerFactory _loggerFactory;
        protected readonly ICurrentUserService _currentUserService;

        protected BaseDbContext(
            DbContextOptions options,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            ICurrentUserService currentUserService) 
            : base(options)
        {
            _configuration = configuration;
            _loggerFactory = loggerFactory;
            _currentUserService = currentUserService;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            
            optionsBuilder.UseNpgsql(
                _configuration.GetConnectionString("PostgreSQL"),
                npgOptions =>
                {
                    npgOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorCodesToAdd: null);
                    npgOptions.CommandTimeout(120);
                    npgOptions.UseAdminDatabase("postgres");
                });

            optionsBuilder.UseLoggerFactory(_loggerFactory);
            
            if (_configuration.GetValue<bool>("EnableSensitiveDataLogging"))
                optionsBuilder.EnableSensitiveDataLogging();
        }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            AddAuditInfo();
            SetConcurrencyTokens();
            return base.SaveChangesAsync(ct);
        }

        private void AddAuditInfo()
        {
            var entries = ChangeTracker.Entries<AuditableEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified);

            foreach (var entry in entries)
            {
                entry.Entity.LastModifiedDate = DateTime.UtcNow;
                entry.Entity.LastModifiedBy = _currentUserService.UserId ?? "system";
                entry.Entity.ModifiedFromIp = _currentUserService.ClientIp ?? "unknown";

                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    entry.Entity.CreatedBy = _currentUserService.UserId ?? "system";
                    entry.Entity.CreatedFromIp = _currentUserService.ClientIp ?? "unknown";
                }
            }
        }

        private void SetConcurrencyTokens()
        {
            foreach (var entry in ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Modified))
            {
                entry.Property("xmin").OriginalValue = entry.Property("xmin").CurrentValue;
            }
        }
    }
}