using System;

namespace ecommerce.Db
{
    public class ContentMetadata
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        // Foreign key to the parent Content
        public Guid ContentId { get; set; }
        
        // Key for the metadata record
        public string Key { get; set; } = string.Empty;
        
        // Value for the metadata record
        public string Value { get; set; } = string.Empty;
        
        // Navigation property back to the parent Content
        public Content Content { get; set; } = null!;
    }
}
