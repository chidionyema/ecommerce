using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ecommerce.Db;
 // This using might be unused if ecommerceContext is solely from ecommerce.Db
using ecommerce.Infrastructure.Repository.Interfaces;

namespace ecommerce.Infrastructure.Repository
{
    public class ProductRepository : IProductRepository // Implements IProductRepository
    {
        private readonly ecommerceContext _context;
        private readonly ILogger<ProductRepository> _logger;
        private readonly IMemoryCache _memoryCache;
        private static readonly ConcurrentDictionary<string, byte> _repositoryCacheKeys = new();

        // Cache settings
        private readonly TimeSpan _defaultCacheDuration = TimeSpan.FromMinutes(10); // Default cache duration
        
        // Cache key patterns/prefixes
        private const string ProductsListKeyPattern = "products_list_p"; // products_list_p{page}_s{size}
        private const string ProductByIdKeyPrefix = "product_"; // product_{id}_cat{bool}_cont{bool}_meta{bool}
        private const string CategoryProductsKeyPattern = "category_{0}_products_p"; // category_{catId}_products_p{page}_s{size}
        private const string ProductsByIdsKeyPrefix = "products_by_ids_"; // products_by_ids_{sorted_ids_string}
        private const string ProductReviewByIdKey = "product_review_{0}"; // product_review_{reviewId}
        private const string ReviewsForProductKeyPattern = "reviews_for_product_{0}_p"; // reviews_for_product_{prodId}_p{page}_s{size}
        private const string PendingReviewsKeyPattern = "pending_reviews_p"; // pending_reviews_p{page}_s{size}
        private const string CategoriesAllListKey = "categories_all_list";
        private const string CategoryByIdKey = "category_id_{0}"; // category_id_{catId}

        public ProductRepository(
            ecommerceContext context,
            ILogger<ProductRepository> logger,
            IMemoryCache memoryCache)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        }

        #region Product Methods
        public async Task<IEnumerable<Product>> GetProductsAsync(int page, int pageSize)
        {
            page = NormalizePage(page);
            pageSize = NormalizePageSize(pageSize);
            var cacheKey = $"{ProductsListKeyPattern}{page}_s{pageSize}";

            return await GetFromCacheAsync<IEnumerable<Product>>(cacheKey, async () =>
            {
                _logger.LogInformation("DB: Fetching products page {Page}, size {PageSize}", page, pageSize);
                return await _context.Products
                    .AsNoTracking()
                    .Include(p => p.Category)
                    .Include(p => p.Contents.Where(c => c.EntityType == nameof(Product)))
                    .OrderBy(p => p.Name)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }) ?? Enumerable.Empty<Product>();
        }

        public async Task<Product?> GetProductByIdAsync(Guid id, bool includeCategory = false, 
            bool includeContents = false, bool includeMetadata = false)
        {
            var cacheKey = $"{ProductByIdKeyPrefix}{id}_cat{includeCategory}_cont{includeContents}_meta{includeMetadata}";

            // FIXED: Changed GetFromCacheAsync<Product?> to GetFromCacheAsync<Product>
            return await GetFromCacheAsync<Product>(cacheKey, async () => 
            {
                _logger.LogInformation("DB: Fetching product {ProductId} with includes (cat:{IncludeCategory}, cont:{IncludeContents}, meta:{IncludeMetadata})", id, includeCategory, includeContents, includeMetadata);
                var query = _context.Products.AsNoTracking().Where(p => p.Id == id);

                if (includeCategory) query = query.Include(p => p.Category);
                if (includeContents) query = query.Include(p => p.Contents.Where(c => c.EntityType == nameof(Product)));
                if (includeMetadata) query = query.Include(p => p.Metadata);

                return await query.FirstOrDefaultAsync();
            });
        }
        
        public async Task<IEnumerable<Product>> GetProductsByCategoryAsync(Guid categoryId, int page, int pageSize)
        {
            page = NormalizePage(page);
            pageSize = NormalizePageSize(pageSize);
            string cacheKey = $"{string.Format(CategoryProductsKeyPattern, categoryId)}p{page}_s{pageSize}";

            return await GetFromCacheAsync<IEnumerable<Product>>(cacheKey, async () =>
            {
                _logger.LogInformation("DB: Fetching products for category {CategoryId}, page {Page}, size {PageSize}.", categoryId, page, pageSize);
                return await _context.Products
                    .AsNoTracking()
                    .Include(p => p.Category) 
                    .Include(p => p.Contents.Where(c => c.EntityType == nameof(Product))) 
                    .Where(p => p.CategoryId == categoryId)
                    .OrderBy(p => p.Name) 
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }) ?? Enumerable.Empty<Product>();
        }

        public async Task<List<Product>> GetProductsByIdsAsync(List<Guid> productIds)
        {
            if (productIds == null || !productIds.Any()) return new List<Product>();
            
            var sortedIds = string.Join("-", productIds.OrderBy(id => id));
            var cacheKey = $"{ProductsByIdsKeyPrefix}{sortedIds}";

            return await GetFromCacheAsync<List<Product>>(cacheKey, async () =>
            {
                _logger.LogInformation("DB: Fetching products by {Count} IDs", productIds.Count);
                return await _context.Products
                    .AsNoTracking()
                    .Include(p => p.Contents.Where(c => c.EntityType == nameof(Product)))
                    .Where(p => productIds.Contains(p.Id))
                    .ToListAsync();
            }) ?? new List<Product>();
        }

        public async Task AddProductAsync(Product product)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            
            try
            {
                _logger.LogInformation("DB: Adding product {ProductName}", product.Name);
                _context.Products.Add(product);
                await SaveChangesAsync();

                await InvalidateCachesForProduct(product.Id, product.CategoryId, isNew: true);
                string simpleCacheKey = $"{ProductByIdKeyPrefix}{product.Id}_catfalse_contfalse_metafalse";
                await SetCacheValueAsync(simpleCacheKey, product);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Error: Error adding product {ProductName}", product.Name);
                throw new ApplicationException("Database error adding product", ex);
            }
        }
        
        public async Task UpdateProductAsync(Product product)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            try
            {
                _logger.LogInformation("DB: Updating product ID {ProductId}.", product.Id);
                var local = _context.Set<Product>().Local.FirstOrDefault(entry => entry.Id.Equals(product.Id));
                if (local != null) 
                {
                    _context.Entry(local).State = EntityState.Detached; 
                }
                
                _context.Products.Attach(product);
                _context.Entry(product).State = EntityState.Modified;
                await SaveChangesAsync();

                await InvalidateCachesForProduct(product.Id, product.CategoryId);
                string simpleCacheKey = $"{ProductByIdKeyPrefix}{product.Id}_catfalse_contfalse_metafalse";
                await SetCacheValueAsync(simpleCacheKey, product);

            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "DB Concurrency Error: Updating product ID {ProductId}.", product.Id);
                throw new ApplicationException($"Concurrency error updating product {product.Id}. The data may have been modified.", ex);
            }
            catch (DbUpdateException ex)
            {
                 _logger.LogError(ex, "DB Error: Updating product ID {ProductId}.", product.Id);
                throw new ApplicationException($"Database error updating product {product.Id}.", ex);
            }
        }

        public async Task UpdateProductStockAsync(Guid productId, int quantityChanged)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var product = await _context.Products.FindAsync(productId); 
                if (product == null) throw new InvalidOperationException($"Product {productId} not found for stock update.");

                if (quantityChanged > 0 && product.StockQuantity < quantityChanged) 
                    throw new InvalidOperationException($"Insufficient stock for {product.Name}. Requested decrease: {quantityChanged}, Available: {product.StockQuantity}.");

                product.StockQuantity -= quantityChanged;
                product.IsInStock = product.StockQuantity > 0; 

                await SaveChangesAsync();
                await transaction.CommitAsync();
                _logger.LogInformation("DB: Stock updated for product {ProductId}. Quantity changed by: {QuantityChanged}. New stock: {ProductStock}", productId, -quantityChanged, product.StockQuantity);

                await InvalidateCachesForProduct(productId, product.CategoryId);
                string simpleCacheKey = $"{ProductByIdKeyPrefix}{productId}_catfalse_contfalse_metafalse";
                var updatedProduct = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId);
                if(updatedProduct != null) await SetCacheValueAsync(simpleCacheKey, updatedProduct);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "DB Error: Error updating stock for product {ProductId}", productId);
                throw; 
            }
        }
        
        public async Task DeleteProductAsync(Guid id)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var product = await _context.Products.FindAsync(id);
                if (product == null)
                {
                    _logger.LogWarning("DB: Product with ID {ProductId} not found for deletion.", id);
                    await transaction.RollbackAsync(); 
                    throw new KeyNotFoundException($"Product with ID {id} not found.");
                }
                Guid? categoryId = product.CategoryId;

                _logger.LogInformation("DB: Deleting product ID {ProductId}.", id);
                _context.Products.Remove(product);
                await SaveChangesAsync();
                await transaction.CommitAsync();

                await InvalidateCachesForProduct(id, categoryId, isDeletion: true); 
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "DB Error: Deleting product ID {ProductId}. It might be referenced by other records.", id);
                throw new ApplicationException($"Database error deleting product {id}. It might be referenced by other records.", ex);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error deleting product ID {ProductId}.", id);
                throw;
            }
        }
        #endregion

        #region Stock Management Methods
        public async Task<bool> ValidateStockAsync(Guid productId, int quantityRequired)
        {
            if (quantityRequired <= 0) return true; 

            var product = await _context.Products
                .AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => new { p.StockQuantity, p.IsInStock }) 
                .FirstOrDefaultAsync();

            if (product == null)
            {
                _logger.LogWarning("ValidateStockAsync: Product with ID {ProductId} not found.", productId);
                return false; 
            }

            bool hasEnoughStock = product.IsInStock && product.StockQuantity >= quantityRequired;
            if (!hasEnoughStock)
            {
                _logger.LogInformation("ValidateStockAsync: Insufficient stock for Product ID {ProductId}. Required: {QuantityRequired}, Available: {ProductStock}", productId, quantityRequired, product.StockQuantity);
            }
            return hasEnoughStock;
        }

        public async Task<bool> DecrementStockAsync(Guid productId, int quantityToDecrement)
        {
            if (quantityToDecrement <= 0)
            {
                _logger.LogWarning("DecrementStockAsync called with zero or negative quantity for Product ID {ProductId}. No action taken, returning true as no decrement was needed.", productId);
                return true; 
            }
            try
            {
                // UpdateProductStockAsync expects a positive 'quantityChanged' to DECREASE stock.
                // It internally handles transactions and cache invalidation.
                await UpdateProductStockAsync(productId, quantityToDecrement);
                _logger.LogInformation("Stock decremented by {QuantityToDecrement} for Product ID {ProductId} via UpdateProductStockAsync.", quantityToDecrement, productId);
                return true; // Operation initiated successfully
            }
            catch(InvalidOperationException ex) // Catch specific exceptions like insufficient stock from UpdateProductStockAsync
            {
                _logger.LogWarning(ex, "Failed to decrement stock for Product ID {ProductId} due to operational issue (e.g., insufficient stock).", productId);
                return false; // Indicate failure due to business rule or data state
            }
            catch(Exception ex) // Catch other potential errors during stock update
            {
                _logger.LogError(ex, "Generic error during DecrementStockAsync for Product ID {ProductId}.", productId);
                return false; // Indicate failure due to unexpected error
            }
        }
        #endregion

        #region Cache Management
        private async Task<T?> GetFromCacheAsync<T>(string cacheKey, Func<Task<T?>> getFromDatabase) where T: class
        {
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out T? cachedValue))
                {
                    _logger.LogDebug("Cache hit for {CacheKey}", cacheKey);
                    return cachedValue;
                }
                _logger.LogDebug("Cache miss for {CacheKey}", cacheKey);
                var value = await getFromDatabase();
                if (value != null)
                {
                    _memoryCache.Set(cacheKey, value, _defaultCacheDuration);
                    _repositoryCacheKeys.TryAdd(cacheKey, 0); 
                    _logger.LogDebug("Value set in cache for {CacheKey}", cacheKey);
                }
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing cache or database for {CacheKey}", cacheKey);
                throw; 
            }
        }
        
        private async Task SetCacheValueAsync<T>(string cacheKey, T value)
        {
            if (value == null) 
            {
                _logger.LogDebug("SetCacheValueAsync called with null value for key {CacheKey}. Not caching.", cacheKey);
                return;
            }
            _memoryCache.Set(cacheKey, value, _defaultCacheDuration);
            _repositoryCacheKeys.TryAdd(cacheKey, 0);
            _logger.LogDebug("Value set in cache for {CacheKey}", cacheKey);
            await Task.CompletedTask; 
        }

        private async Task InvalidateCachesForProduct(Guid productId, Guid? categoryId, bool isDeletion = false, bool isNew = false)
        {
            var includeFlags = new[] { false, true };
            var productKeysToRemove = new List<string>();
            foreach (var cat in includeFlags)
            foreach (var cont in includeFlags)
            foreach (var meta in includeFlags)
            {
                productKeysToRemove.Add($"{ProductByIdKeyPrefix}{productId}_cat{cat}_cont{cont}_meta{meta}");
            }
            // Ensure the most basic key (all flags false) for GetProductByIdAsync is also considered for removal
            // if not already generated by the loop (it is, if includeFlags covers false).
            // Adding it explicitly for clarity or if includeFlags logic changes.
            productKeysToRemove.Add($"{ProductByIdKeyPrefix}{productId}_catfalse_contfalse_metafalse");


            await RemoveFromCacheAsync(productKeysToRemove.Distinct().ToList());
            
            if (categoryId.HasValue)
            {
                // Invalidate products by specific category (paginated lists)
                await RemoveCacheByPatternAsync(string.Format(CategoryProductsKeyPattern, categoryId.Value).TrimEnd('p') ); 
            }
            
            // Invalidate general product lists and products by ID lists
            await RemoveCacheByPatternAsync(ProductsListKeyPattern); 
            await RemoveCacheByPatternAsync(ProductsByIdsKeyPrefix); 
            
            _logger.LogDebug("Invalidated caches for Product ID {ProductId}, Category ID {CategoryId}. IsDeletion: {IsDeletion}, IsNew: {IsNew}", productId, categoryId, isDeletion, isNew);
        }

        private async Task RemoveFromCacheAsync(string key) 
        {
            _memoryCache.Remove(key);
            _repositoryCacheKeys.TryRemove(key, out _);
            _logger.LogDebug("Removed single key from cache: {CacheKey}", key);
            await Task.CompletedTask; 
        }

        private async Task RemoveFromCacheAsync(IEnumerable<string> keys) 
        {
            foreach (var key in keys.Distinct()) 
            {
                _memoryCache.Remove(key);
                _repositoryCacheKeys.TryRemove(key, out _);
            }
            _logger.LogDebug("Attempted to remove multiple keys from cache. Count: {KeyCount}", keys.Count());
            await Task.CompletedTask; 
        }

        private async Task RemoveCacheByPatternAsync(string patternPrefix)
        {
            if (string.IsNullOrEmpty(patternPrefix)) return;

            var keysToRemove = _repositoryCacheKeys.Keys
                .Where(k => k.StartsWith(patternPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (keysToRemove.Any())
            {
                _logger.LogDebug("Removing {Count} cache entries starting with pattern '{PatternPrefix}'. Example: {FirstKey}", keysToRemove.Count, patternPrefix, keysToRemove.First());
                await RemoveFromCacheAsync(keysToRemove); 
            }
        }
        #endregion

        #region Core Database Operations
        // This method is now public to implement the interface member
        public async Task SaveChangesAsync() 
        {
            try
            {
                _logger.LogInformation("DB: Saving changes to database");
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "DB Concurrency error saving changes");
                throw; 
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Update error saving changes");
                throw; 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving changes to database");
                throw; 
            }
        }

        private int NormalizePage(int page) => Math.Max(1, page);
        private int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 1, 100); 
        #endregion

        #region Product Review Methods
        public async Task AddProductReviewAsync(ProductReview review)
        {
            if (review == null) throw new ArgumentNullException(nameof(review));
            if (review.ProductId == Guid.Empty) throw new ArgumentException("Invalid product ID for review.");

            try
            {
                _logger.LogInformation("DB: Adding review for product {ProductId}", review.ProductId);
                _context.ProductReviews.Add(review);
                await SaveChangesAsync();
                
                await InvalidateReviewCaches(review.ProductId, !review.IsApproved); 
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Error: Error adding review for product {ProductId}", review.ProductId);
                throw new ApplicationException("Database error adding review", ex);
            }
        }

        public async Task<ProductReview?> GetProductReviewByIdAsync(Guid reviewId)
        {
            var cacheKey = string.Format(ProductReviewByIdKey, reviewId);
            // FIXED: Changed GetFromCacheAsync<ProductReview?> to GetFromCacheAsync<ProductReview>
            return await GetFromCacheAsync<ProductReview>(cacheKey, async () => 
            {
                _logger.LogInformation("DB: Fetching review {ReviewId}", reviewId);
                return await _context.ProductReviews
                    .AsNoTracking()
                    .Include(r => r.Product) 
                    .FirstOrDefaultAsync(r => r.Id == reviewId);
            });
        }

        public async Task<IEnumerable<ProductReview>> GetProductReviewsAsync(Guid productId, int page, int pageSize)
        {
            page = NormalizePage(page);
            pageSize = NormalizePageSize(pageSize);
            var cacheKey = $"{string.Format(ReviewsForProductKeyPattern, productId)}p{page}_s{pageSize}";

            return await GetFromCacheAsync<IEnumerable<ProductReview>>(cacheKey, async () =>
            {
                _logger.LogInformation("DB: Fetching approved reviews for product {ProductId}, page {Page}, size {PageSize}", productId, page, pageSize);
                return await _context.ProductReviews
                    .AsNoTracking()
                    .Where(r => r.ProductId == productId && r.IsApproved)
                    .OrderByDescending(r => r.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }) ?? Enumerable.Empty<ProductReview>();
        }

        public async Task<IEnumerable<ProductReview>> GetPendingReviewsAsync(int page, int pageSize)
        {
            page = NormalizePage(page);
            pageSize = NormalizePageSize(pageSize);
            var cacheKey = $"{PendingReviewsKeyPattern}p{page}_s{pageSize}";

            return await GetFromCacheAsync<IEnumerable<ProductReview>>(cacheKey, async () =>
            {
                _logger.LogInformation("DB: Fetching pending reviews, page {Page}, size {PageSize}", page, pageSize);
                return await _context.ProductReviews
                    .AsNoTracking()
                    .Include(r => r.Product) 
                    .Where(r => !r.IsApproved)
                    .OrderByDescending(r => r.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }) ?? Enumerable.Empty<ProductReview>();
        }

        public async Task UpdateProductReviewAsync(ProductReview review)
        {
            if (review == null) throw new ArgumentNullException(nameof(review));
            
            try
            {
                _logger.LogInformation("DB: Updating review {ReviewId}", review.Id);
                var existing = await _context.ProductReviews.FindAsync(review.Id);
                if (existing == null) throw new KeyNotFoundException($"Review with ID {review.Id} not found.");

                existing.Title = review.Title;
                existing.Comment = review.Comment;
                existing.Rating = review.Rating;
                // Assuming IsApproved is not updated here directly, but via ApproveProductReviewAsync
                // If it can be updated here, the logic for InvalidateReviewCaches might need adjustment
                // based on old vs new approval state.

                _context.ProductReviews.Update(existing); 
                await SaveChangesAsync();
                
                await SetCacheValueAsync(string.Format(ProductReviewByIdKey, review.Id), existing);
                // If approval status cannot change here, then `!existing.IsApproved` is fine.
                // If it could change, you'd need the old approval state.
                await InvalidateReviewCaches(review.ProductId, !existing.IsApproved); 
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "DB Concurrency error updating review {ReviewId}", review.Id);
                throw new ApplicationException("Review was modified by another operation", ex);
            }
        }

        public async Task DeleteProductReviewAsync(Guid reviewId)
        {
            try
            {
                _logger.LogInformation("DB: Deleting review {ReviewId}", reviewId);
                var review = await _context.ProductReviews.FindAsync(reviewId);
                if (review == null) 
                {
                    _logger.LogWarning("DB: Review {ReviewId} not found for deletion.", reviewId);
                    return;
                }
                Guid? productId = review.ProductId; // Capture before deletion
                bool wasPending = !review.IsApproved; // Capture before deletion

                _context.ProductReviews.Remove(review);
                await SaveChangesAsync();
                
                await RemoveFromCacheAsync(string.Format(ProductReviewByIdKey, reviewId));
                if(productId.HasValue) await InvalidateReviewCaches(productId.Value, wasPending);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Error: Error deleting review {ReviewId}", reviewId);
                throw new ApplicationException("Database error deleting review", ex);
            }
        }

        public async Task<bool> ApproveProductReviewAsync(Guid reviewId, bool isApproved)
        {
            try
            {
                _logger.LogInformation("DB: Setting approval for review {ReviewId} to {Status}", reviewId, isApproved);
                var review = await _context.ProductReviews.FindAsync(reviewId);
                if (review == null) 
                {
                    _logger.LogWarning("DB: Review {ReviewId} not found for approval.", reviewId);
                    return false;
                }

                bool approvalStateChanged = review.IsApproved != isApproved;
                review.IsApproved = isApproved;
                // _context.ProductReviews.Update(review); // Mark entity as modified if not automatically tracked by FindAsync + property change
                _context.Entry(review).State = EntityState.Modified; // Explicitly mark as modified
                await SaveChangesAsync();
                
                await SetCacheValueAsync(string.Format(ProductReviewByIdKey, reviewId), review);
                if(approvalStateChanged)
                {
                     // Invalidate both pending and approved lists for the product as review moved between states
                     await InvalidateReviewCaches(review.ProductId, true); 
                }
                return true;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Error: Error approving review {ReviewId}", reviewId);
                return false; 
            }
        }
        #endregion

        #region Category Methods
        public async Task<IEnumerable<Category>> GetCategoriesAsync()
        {
            return await GetFromCacheAsync<IEnumerable<Category>>(CategoriesAllListKey, async () =>
            {
                _logger.LogInformation("DB: Fetching all categories");
                return await _context.Categories
                    .AsNoTracking()
                    .OrderBy(c => c.Name)
                    .ToListAsync();
            }) ?? Enumerable.Empty<Category>();
        }

        public async Task<Category?> GetCategoryByIdAsync(Guid categoryId)
        {
            var cacheKey = string.Format(CategoryByIdKey, categoryId);
            // FIXED: Changed GetFromCacheAsync<Category?> to GetFromCacheAsync<Category>
            return await GetFromCacheAsync<Category>(cacheKey, async () => 
            {
                _logger.LogInformation("DB: Fetching category {CategoryId}", categoryId);
                return await _context.Categories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == categoryId);
            });
        }

        public async Task AddCategoryAsync(Category category)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            
            try
            {
                _logger.LogInformation("DB: Adding category {CategoryName}", category.Name);
                _context.Categories.Add(category);
                await SaveChangesAsync();
                
                await SetCacheValueAsync(string.Format(CategoryByIdKey, category.Id), category);
                await InvalidateCategoryCaches(invalidateListAlso: true); 
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB Error: Error adding category {CategoryName}", category.Name);
                throw new ApplicationException("Database error adding category", ex);
            }
        }

        public async Task UpdateCategoryAsync(Category category)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            
            try
            {
                _logger.LogInformation("DB: Updating category {CategoryId}", category.Id);
                var existing = await _context.Categories.FindAsync(category.Id); // Fetches and starts tracking
                if (existing == null) throw new KeyNotFoundException($"Category with ID {category.Id} not found.");

                // Apply changes to the tracked entity
                existing.Name = category.Name; 
                // EF Core tracks changes on entities fetched from the context.
                // No need to call _context.Categories.Update(existing) if 'existing' is tracked and modified.
                // However, explicitly setting state can be clearer or useful if 'existing' was detached.
                // _context.Entry(existing).State = EntityState.Modified; // Redundant if 'existing' is tracked and properties changed.

                await SaveChangesAsync();
                
                await SetCacheValueAsync(string.Format(CategoryByIdKey, category.Id), existing); // Cache the updated entity
                await InvalidateCategoryCaches(invalidateListAlso: true); 
                await InvalidateAllProductListCaches(); // Category name change might affect product listings if they display it
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "DB Concurrency error updating category {CategoryId}", category.Id);
                throw new ApplicationException("Category was modified by another operation", ex);
            }
        }

        public async Task DeleteCategoryAsync(Guid categoryId)
        {
            try
            {
                _logger.LogInformation("DB: Deleting category {CategoryId}", categoryId);
                var category = await _context.Categories
                    .Include(c => c.Products) // Include products to check for associations
                    .FirstOrDefaultAsync(c => c.Id == categoryId);

                if (category == null) 
                {
                     _logger.LogWarning("DB: Category {CategoryId} not found for deletion.", categoryId);
                    return; // Or throw KeyNotFoundException
                }
                if (category.Products?.Any() == true)
                {
                    _logger.LogError("DB: Cannot delete category {CategoryId} as it has associated products.", categoryId);
                    throw new InvalidOperationException($"Cannot delete category '{category.Name}' as it has associated products. Reassign products first.");
                }

                _context.Categories.Remove(category);
                await SaveChangesAsync();
                
                await RemoveFromCacheAsync(string.Format(CategoryByIdKey, categoryId));
                await InvalidateCategoryCaches(invalidateListAlso: true); 
                await InvalidateAllProductListCaches(); // Products in this category are now uncategorized or gone.
            }
            catch (DbUpdateException ex) // Could be foreign key constraint if Products wasn't checked or other DB issues
            {
                _logger.LogError(ex, "DB Error: Error deleting category {CategoryId}", categoryId);
                throw new ApplicationException("Database error deleting category. It might be referenced by other records.", ex);
            }
        }
        #endregion

        #region Cache Invalidation Helpers
        private async Task InvalidateReviewCaches(Guid productId, bool invalidatePendingListAlso)
        {
            // Invalidate cache for approved reviews for this product
            await RemoveCacheByPatternAsync(string.Format(ReviewsForProductKeyPattern, productId)); 
            if (invalidatePendingListAlso)
            {
                // Invalidate cache for all pending reviews (as one might have moved out of this list)
                await RemoveCacheByPatternAsync(PendingReviewsKeyPattern); 
            }
            _logger.LogDebug("Invalidated review caches for ProductId: {ProductId}, PendingListAlso: {InvalidatePendingListAlso}", productId, invalidatePendingListAlso);
        }

        private async Task InvalidateCategoryCaches(bool invalidateListAlso = false)
        {
            if(invalidateListAlso)
            {
                await RemoveFromCacheAsync(CategoriesAllListKey); 
            }
            // Invalidate individual category caches
            await RemoveCacheByPatternAsync(CategoryByIdKey.Replace("{0}", "")); 
            _logger.LogDebug("Invalidated category caches. List invalidated: {InvalidateListAlso}", invalidateListAlso);
        }

        private async Task InvalidateAllProductListCaches() 
        {
            _logger.LogInformation("Attempting to invalidate ALL product list caches due to potential cascading impact (e.g., category update/delete).");
            await RemoveCacheByPatternAsync(ProductsListKeyPattern); 
            await RemoveCacheByPatternAsync(ProductsByIdsKeyPrefix); 
            // Invalidate products by category lists (all categories)
            await RemoveCacheByPatternAsync(CategoryProductsKeyPattern.Replace("{0}_products_p","category_")); // More targeted prefix
        }
        #endregion
    }
}