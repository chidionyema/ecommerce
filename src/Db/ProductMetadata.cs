using System;

namespace ecommerce.Db
{
    public class ProductMetadata : AuditableEntity
    {
        // The Id property is inherited from AuditableEntity

        // Foreign key to the associated product
        public Guid ProductId { get; set; }

        // A "key" (like "CourseInfo", "AuthorInfo", "CourseCurriculum", etc.)
        public string KeyName{ get; set; } = string.Empty;

        // The string value (could be JSON, or just a single text)
        public string KeyValue { get; set; } = string.Empty;

        // Navigation property back to the product
        public Product Product { get; set; } = null!;
    }
}
