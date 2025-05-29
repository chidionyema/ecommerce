using System;
using System.ComponentModel.DataAnnotations; // Add this for validation attributes

namespace ecommerce.Dto
{
    // Add validation using data annotations
    public record ChunkSessionRequest(
    Guid EntityId,
    int ChunkSize, // You might want to add a [Range] attribute here too, e.g., [Range(1024 * 64, 1024 * 1024 * 10)]
    [Required(ErrorMessage = "FileName is required.")]
    string FileName,
    
    [Required(ErrorMessage = "ContentType is required.")] // <-- ADD THIS LINE
    string ContentType,                                  // <-- ADD THIS LINE

    [Range(1, int.MaxValue, ErrorMessage = "TotalChunks must be greater than or equal to 1.")]
    int TotalChunks,
    
    [Range(1, long.MaxValue, ErrorMessage = "TotalSize must be greater than or equal to 1.")] // Note: This implies empty files are not allowed for chunked uploads.
    long TotalSize
);
    public record StorageInfo(long FileSize)
    {
        public string BucketName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public string StorageDetails { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public record ChunkSessionDto(
        Guid SessionId,
        DateTime ExpiresAt,
        int TotalChunks);

    public record ContentDto(
        Guid Id,
        Guid EntityId,
        string EntityType,
        string Url,
        string ContentType,
        long FileSize);

    public record ContentUploadResult(
        string BucketName,
        string ObjectName,
        string ContentType,
        long FileSize,
        string VersionId,
        string StorageDetails,
        string Path
    );
}
