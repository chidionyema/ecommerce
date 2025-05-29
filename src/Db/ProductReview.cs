using System;

namespace ecommerce.Db
{
    public class ProductReview : AuditableEntity
    {
        // Parameterless constructor for EF Core
        public ProductReview() { }

        // Constructor to initialize review details
        public ProductReview(string comment, double rating)
        {
            Comment = comment;
            Rating = rating;
        }

        // User ID of the reviewer (foreign key to User table)
        public Guid? UserId { get; set; }

        public string? Title { get; set; }

        public string? Email { get; set; }

        // Review comment
        public string Comment { get; set; } = string.Empty;

        // Rating value
        public double Rating { get; set; }

        // Foreign key for the associated product
        public Guid ProductId { get; set; }

        // Navigation property to the Product
        public Product? Product { get; set; }

        public bool IsApproved { get; set; }
        
        public bool IsVerifiedPurchase { get; set; }
    }
}