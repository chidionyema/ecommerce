using System;
using System.Collections.Generic;

namespace ecommerce.Dto
{
    public record ProductDto
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Headline { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ShortDescription { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public decimal UnitPrice { get; init; }
          public decimal OriginalPrice { get; set; }
        public int Stock { get; init; }
        public bool IsListed { get; init; }
        public bool IsFeatured { get; init; }
        public double Rating { get; init; }
        public double? AverageRating { get; set; }
        public bool IsInStock { get; init; }
        public string Brand { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public Guid CategoryId { get; init; }
        public List<ContentDto> Contents { get; init; } = new();
        public List<ProductMetadataDto> Metadata { get; init; } = new();
         public List<ProductReviewDto> Reviews { get; init; } = new();
        public List<ProductSpecificationDto> Specifications { get; init; } = new();

        
    }
}
