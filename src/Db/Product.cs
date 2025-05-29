using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

namespace ecommerce.Db
{
    public class Product : AuditableEntity
    {
        public Product(Guid id, string name, string description, decimal price, Guid categoryId) : base(id)
        {
            Name = name;
            Description = description;
            UnitPrice = price;
            CategoryId = categoryId;
        }

        public Product(string name, string description, decimal price, Guid categoryId) : base()
        {
            Name = name;
            Description = description;
            UnitPrice = price;
            CategoryId = categoryId;
        }

        public Product() : base() { }

        public Guid? AuthorProfileId { get; set; }
        public UserProfile? AuthorProfile { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Headline { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ShortDescription { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public decimal OriginalPrice { get; set; }
        public double Rating { get; set; }
        public bool IsListed { get; set; }
        public bool IsFeatured { get; set; }
        public int StockQuantity { get; set; }
        public bool IsInStock { get; set; }
        public string? Brand { get; set; } = string.Empty;
        public string? Type { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }

        [NotMapped]
        public double? AverageRating => Reviews != null && Reviews.Count > 0 
            ? Math.Round(Reviews.Where(r => r.IsApproved).Average(r => r.Rating), 1) 
            : null;
        public Guid CategoryId { get; set; }
        public Category? Category { get; set; }
        public List<Content>? Contents { get; set; } 
        public List<ProductReview>? ProductReviews { get; set; }
        public List<ProductMetadata>? Metadata { get; set; }
        public List<ProductSpecification>? Specifications { get; set; }
        
        // This property is for mapping and backward compatibility
        [NotMapped]
        public ICollection<ProductReview>? Reviews => ProductReviews;
    }
}