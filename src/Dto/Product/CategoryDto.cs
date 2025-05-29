using System;

namespace ecommerce.Dto
{
    public record CategoryDto
    {
        public Guid Id { get; init; }
        public string? Name { get; init; } = string.Empty;
    }
}
