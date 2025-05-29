using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ecommerce.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ecommerce.Services;
using Npgsql;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ecommerce.Db
{
    public class ecommerceContext : IdentityDbContext<User>, IHealthCheck
    {
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<ecommerceContext> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _hostEnvironment;

        // Entity Sets
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<OrderItem> OrderItems => Set<OrderItem>();
        public DbSet<Content> Contents => Set<Content>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<ProductMetadata> ProductMetadata => Set<ProductMetadata>();
        public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
        public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<ContentMetadata> ContentMetadata => Set<ContentMetadata>();
        public DbSet<GuestOrderInfo> GuestOrderInfos => Set<GuestOrderInfo>();
        public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

        public ecommerceContext(
            DbContextOptions<ecommerceContext> options,
            ICurrentUserService currentUserService,
            ILogger<ecommerceContext> logger,
            IHttpContextAccessor httpContextAccessor,
            IConfiguration configuration,
            IHostEnvironment hostEnvironment) : base(options)
        {
            _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
            
            ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            ChangeTracker.LazyLoadingEnabled = false;
            // Removed CascadeDeleteTiming as it's not available in this EF Core version.
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseNpgsql(
                    _configuration.GetConnectionString("PostgreSQLConnection"),
                    npgOptions =>
                    {
                        npgOptions.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(30),
                            errorCodesToAdd: null);
                        npgOptions.CommandTimeout(60);
                    });
            }

            if (_hostEnvironment.IsDevelopment())
            {
                optionsBuilder.EnableDetailedErrors();
                optionsBuilder.EnableSensitiveDataLogging();
                optionsBuilder.LogTo(
                    msg => _logger.LogInformation(msg),
                    new[] { DbLoggerCategory.Database.Command.Name },
                    LogLevel.Information);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Configure PostgreSQL xmin concurrency tokens for Product and Order
            // Fixed: Use byte[] instead of uint for PostgreSQL xmin
            modelBuilder.Entity<Product>(entity =>
            {
                entity.Property<byte[]>("xmin")
                      .HasColumnType("xmin")
                      .IsRowVersion()
                      .IsConcurrencyToken()
                      .ValueGeneratedOnAddOrUpdate();
            });

            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property<byte[]>("xmin")
                      .HasColumnType("xmin")
                      .IsRowVersion()
                      .IsConcurrencyToken()
                      .ValueGeneratedOnAddOrUpdate();
            });

            // Payment Configuration
            modelBuilder.Entity<Payment>(entity =>
            {
                entity.Property(p => p.Amount)
                      .HasColumnType("numeric(18,2)");
            });

            // Index Configuration for Product using Npgsql extension.
            var productIndex = modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.CategoryId, p.IsListed, p.IsFeatured });
            Microsoft.EntityFrameworkCore.NpgsqlIndexBuilderExtensions.IncludeProperties(
                productIndex, (Product p) => new { p.UnitPrice, p.StockQuantity })
                .HasDatabaseName("IX_Products_CategoryListedFeatured");

            modelBuilder.Entity<Order>()
                .HasIndex(o => new { o.Status, o.CreatedAt })
                .HasDatabaseName("IX_Orders_StatusCreatedAt");

            // Data Validation for Product
            modelBuilder.Entity<Product>(entity =>
            {
                entity.Property(p => p.Name)
                      .HasMaxLength(200)
                      .IsRequired()
                      .HasComment("Product display name");
                
                entity.Property(p => p.Rating)
                      .HasPrecision(3, 2)
                      .HasDefaultValue(0);
                
                // Fixed: Use PostgreSQL syntax for check constraint
                entity.ToTable(t => t.HasCheckConstraint("CK_Product_Rating", "\"Rating\" BETWEEN 0 AND 5"));
            });

            // UserProfile Configuration (ensure LastLogin exists)
            modelBuilder.Entity<UserProfile>(entity =>
            {
                entity.HasIndex(up => up.UserId)
                      .IsUnique()
                      .HasFilter("\"UserId\" IS NOT NULL");
                
                entity.Property(up => up.LastLogin)
                      .HasDefaultValueSql("NOW()");
            });

            // GuestOrderInfo Configuration
            modelBuilder.Entity<GuestOrderInfo>(entity =>
            {
                entity.HasKey(g => g.Id);
                
                entity.Property(g => g.Email)
                      .HasMaxLength(256)
                      .IsRequired();
                
                entity.Property(g => g.FirstName)
                      .HasMaxLength(100)
                      .IsRequired();
                
                entity.Property(g => g.LastName)
                      .HasMaxLength(100)
                      .IsRequired();
                
                entity.Property(g => g.Address)
                      .HasMaxLength(500);
                
                entity.Property(g => g.City)
                      .HasMaxLength(100);
                
                entity.Property(g => g.State)
                      .HasMaxLength(100);
                
                entity.Property(g => g.PostalCode)
                      .HasMaxLength(20);
                
                entity.Property(g => g.Country)
                      .HasMaxLength(100);
                
                entity.Property(g => g.Phone)
                      .HasMaxLength(20);
                
                entity.Property(g => g.OrderToken)
                      .HasMaxLength(100)
                      .IsRequired();
                
                entity.HasIndex(g => g.OrderToken)
                      .IsUnique()
                      .HasDatabaseName("IX_GuestOrderInfo_OrderToken");
                
                entity.HasIndex(g => g.OrderId)
                      .IsUnique()
                      .HasDatabaseName("IX_GuestOrderInfo_OrderId");
                
                entity.HasIndex(g => g.Email)
                      .HasDatabaseName("IX_GuestOrderInfo_Email");
                
                // Configure relationship with Order
                entity.HasOne(g => g.Order)
                      .WithOne()
                      .HasForeignKey<GuestOrderInfo>(g => g.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Relationships
            modelBuilder.Entity<OrderItem>()
                .HasOne(oi => oi.Order)
                .WithMany(o => o.OrderItems)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<RefreshToken>(entity =>
            {
                entity.HasIndex(rt => rt.Token)
                      .IsUnique();
                
                entity.Property(rt => rt.Expires)
                      .HasDefaultValueSql("NOW() + INTERVAL '7 days'");
            });

            modelBuilder.Entity<RevokedToken>(entity =>
            {
                entity.HasIndex(rt => rt.Token)
                    .IsUnique()
                    .HasDatabaseName("IX_RevokedTokens_Token");
                
                entity.HasIndex(rt => rt.RevokedAt)
                    .HasDatabaseName("IX_RevokedTokens_RevokedAt");
                
                entity.HasIndex(rt => rt.ExpiresAt)
                    .HasDatabaseName("IX_RevokedTokens_ExpiresAt");
                
                entity.Property(rt => rt.RevokedAt)
                    .HasDefaultValueSql("NOW()");
                
                // Optional: Configure relationship with User if needed
                entity.HasOne(rt => rt.User)
                    .WithMany()
                    .HasForeignKey(rt => rt.UserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            if (_hostEnvironment.IsDevelopment())
            {
                SeedDevelopmentData(modelBuilder);
            }
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            
            try
            {
                AuditEntities();
                var result = await base.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Concurrency conflict detected. User: {UserId}", _currentUserService.UserId);
                throw new ConcurrencyException("Data conflict detected. Please refresh and try again.", ex);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Unique constraint violation. User: {UserId}", _currentUserService.UserId);
                throw new DataUpdateException("Duplicate entry detected. Please check your data.", ex);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogCritical(ex, "Database error. Path: {Path}",
                    _httpContextAccessor.HttpContext?.Request.Path);
                throw;
            }
        }

        private void AuditEntities()
        {
            var now = DateTime.UtcNow;
            var userId = _currentUserService.UserId ?? "system";
            var ipAddress = _currentUserService.ClientIp ?? "unknown";

            foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = userId;
                    entry.Entity.CreatedFromIp = ipAddress;
                }

                if (entry.State == EntityState.Modified || entry.State == EntityState.Deleted)
                {
                    entry.Entity.LastModifiedDate = now;
                    entry.Entity.LastModifiedBy = userId;
                    entry.Entity.ModifiedFromIp = ipAddress;
                }
            }
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var data = new Dictionary<string, object>
            {
                { "connections", Database.GetDbConnection().ConnectionString },
                { "pending_migrations", (await Database.GetPendingMigrationsAsync(cancellationToken)).Count() }
            };

            try
            {
                if (!await Database.CanConnectAsync(cancellationToken))
                    return HealthCheckResult.Unhealthy("Cannot connect", data: data);

                await Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
                return HealthCheckResult.Healthy("OK", data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database health check failed");
                return HealthCheckResult.Unhealthy("Connection failure", ex, data);
            }
        }

        private static void SeedDevelopmentData(ModelBuilder modelBuilder)
        {
            var categoryId = Guid.NewGuid();
            var productId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            // Ensure this is a string for UserProfile, since UserProfile.UserId is string.
            var userId = Guid.NewGuid().ToString(); 

            modelBuilder.Entity<Category>().HasData(
                new Category
                {
                    Id = categoryId,
                    Name = "Development Category",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "seed",
                    RowVersion = new byte[] { 0 }
                });

            modelBuilder.Entity<Product>().HasData(
                new Product
                {
                    Id = productId,
                    Name = "Test Product",
                    UnitPrice = 9.99m,
                    StockQuantity = 100,
                    CategoryId = categoryId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "seed",
                    RowVersion = new byte[] { 0 }
                });

            

            // Fixed: Use OrderStatus enum instead of string
            modelBuilder.Entity<Order>().HasData(
                new Order
                {
                    Id = orderId,
                    Status = OrderStatus.Pending, // Use enum value instead of string
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "seed",
                    RowVersion = new byte[] { 0 }
                });

            modelBuilder.Entity<GuestOrderInfo>().HasData(
                new GuestOrderInfo
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    Email = "guest@example.com",
                    FirstName = "Guest",
                    LastName = "User",
                    Address = "123 Test Street",
                    City = "Test City",
                    State = "Test State",
                    PostalCode = "12345",
                    Country = "Test Country",
                    Phone = "123-456-7890",
                    OrderToken = Guid.NewGuid().ToString("N")[..16] // Generate a 16-character token
                });
        }
    }
}