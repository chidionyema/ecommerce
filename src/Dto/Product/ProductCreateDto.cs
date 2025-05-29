using System;
using System.Collections.Generic;

namespace ecommerce.Dto
{
    public record ProductCreateDto
    {
        public string Name { get; init; } = string.Empty;
        public string Headline { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ShortDescription { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public decimal UnitPrice { get; init; }
        public int Stock { get; init; }
        public double Rating { get; init; }
        public bool IsNew { get; init; }
        public bool InStock { get; init; }
        public string Brand { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public Guid CategoryId { get; init; }
        public List<ProductMetadataDto> Metadata { get; init; } = new();
         public List<ProductSpecificationCreateDto> Specifications { get; init; } = new();
    }
}
