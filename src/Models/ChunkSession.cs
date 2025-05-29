using System;
using System.Collections.Generic;

namespace ecommerce.Models
{
    public class ChunkSession
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public string FileName { get; set; } = null!;
        public int TotalChunks { get; set; }
        public long TotalSize { get; set; }
        public bool IsCompleted { get; set; }
        public HashSet<int> UploadedChunks { get; set; } = new();
        public DateTime ExpiresAt { get; set; }
    }

    public record VirusScanResult(bool IsMalicious, string? ThreatName);
    public record FileSignatureValidationResult(bool IsValid, string FileType);
}