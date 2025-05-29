using System;
using System.Collections.Generic;
using ecommerce.Models;

namespace ecommerce.Db
{
    public class Content : AuditableEntity
    {
        public Content(Guid id) : base(id) { }
        public Content() : base() { }
        
        public Guid EntityId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public ContentType ContentType { get; set; }  
        public string BlobName { get; set; } = string.Empty;  
        public string FileName { get; set; } = string.Empty;  
        public string BucketName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string StorageDetails { get; set; } = string.Empty;  
        public string Path { get; set; } = string.Empty; 
        public string Url { get; set; } = string.Empty;
        public ICollection<ContentMetadata> Metadata { get; set; } = new List<ContentMetadata>();

        public ICollection<ContentVersion> Versions { get; set; } = new List<ContentVersion>();
        
    }
}
