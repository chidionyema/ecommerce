using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ecommerce.Db; // Assuming your entities like ProductReview, Category are here

namespace ecommerce.Infrastructure.Repository.Interfaces
{
    public interface IProductRepository // Assuming this is your existing interface
    {
        // Existing Product methods (examples, add your actual existing ones)
        Task<IEnumerable<Product>> GetProductsAsync(int page, int pageSize);
        Task<Product?> GetProductByIdAsync(Guid id, bool includeCategory = false, bool includeContents = false, bool includeMetadata = false);
        Task<IEnumerable<Product>> GetProductsByCategoryAsync(Guid categoryId, int page, int pageSize);
        Task<List<Product>> GetProductsByIdsAsync(List<Guid> productIds);
        Task AddProductAsync(Product product);
        Task UpdateProductAsync(Product product);
        Task UpdateProductStockAsync(Guid productId, int quantityChanged);
        Task DeleteProductAsync(Guid id);

        Task<bool> ValidateStockAsync(Guid productId, int quantity);
        Task<bool> DecrementStockAsync(Guid productId, int quantity);
    
        // New Methods for Product Reviews
        Task AddProductReviewAsync(ProductReview review);
        Task<ProductReview?> GetProductReviewByIdAsync(Guid reviewId);
        Task<IEnumerable<ProductReview>> GetProductReviewsAsync(Guid productId, int page, int pageSize); // Renamed for clarity
        Task<IEnumerable<ProductReview>> GetPendingReviewsAsync(int page, int pageSize); // Useful for moderation
        Task UpdateProductReviewAsync(ProductReview review);
        Task DeleteProductReviewAsync(Guid reviewId);
        Task<bool> ApproveProductReviewAsync(Guid reviewId, bool isApproved);

        // New Methods for Categories
        Task<IEnumerable<Category>> GetCategoriesAsync();
        Task<Category?> GetCategoryByIdAsync(Guid categoryId);
        Task AddCategoryAsync(Category category);
        Task UpdateCategoryAsync(Category category); // Added for completeness
        Task DeleteCategoryAsync(Guid categoryId);  
        Task SaveChangesAsync(); // Added for completeness
    }
}