using System;

namespace ecommerce.Db
{
    public class ProductSpecification
    {
        public Guid Id { get; set; }
        public Guid ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        
        // Navigation property
        public Product? Product { get; set; }
    }
}
