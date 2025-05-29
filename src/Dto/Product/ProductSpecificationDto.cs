using System;

namespace ecommerce.Dto
{
    public record ProductSpecificationDto
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public int DisplayOrder { get; init; }
        public Guid ProductId { get; init; }
    }
    
    public record ProductSpecificationCreateDto
    {
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public int DisplayOrder { get; init; } = 0;
        public Guid ProductId { get; init; }
    }
}
