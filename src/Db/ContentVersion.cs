using System;

namespace ecommerce.Db
{
    public class ContentVersion : AuditableEntity
    {
        // Foreign key linking back to the Content entity
        public Guid ContentId { get; set; }
        
        // Navigation property back to the Content
        public Content Content { get; set; } = null!;
        
        // Example property for version information (e.g., version number, description, etc.)
        public string VersionInfo { get; set; } = string.Empty;
    }
}
