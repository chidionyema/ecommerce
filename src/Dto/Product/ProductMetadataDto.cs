using System;

namespace ecommerce.Dto
{
    public record ProductMetadataDto
    {
        public Guid Id { get; init; }
        public string KeyName { get; init; } = string.Empty;
        public string KeyValue { get; init; } = string.Empty;
    }
}
