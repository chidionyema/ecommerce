using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ecommerce.Dto;
using ecommerce.Db;
using ecommerce.Models;
using Microsoft.AspNetCore.Http;

namespace ecommerce.Contracts
{
    public interface IContentService
    {
        Task<Content> UploadFileAsync(FileUploadRequest request);
        Task<FileSignatureValidationResult> ValidateFileSignatureAsync(Stream fileStream);
        Task<VirusScanResult> ScanForVirusesAsync(Stream fileStream);
    }

    public record FileUploadRequest(IFormFile File, string UserId, Guid EntityId);
    
    public interface IChunkedUploadService
    {
        Task<ChunkSession> InitSessionAsync(ChunkSessionRequest request);
        Task ProcessChunkAsync(Guid sessionId, int chunkIndex, Stream chunkData);
        Task<Content> CompleteSessionAsync(Guid sessionId, string userId);
        Task<ChunkSession> GetSessionAsync(Guid sessionId);
    }

     public interface IContentStorageService
    {
        Task<ContentUploadResult> UploadAsync(
            Stream fileStream,
            string bucketName,
            string objectName,
            string contentType,
            IDictionary<string, string> metadata,
            CancellationToken cancellationToken = default); // ← Add CancellationToken

        Task<string> GetPresignedUrlAsync(
            string bucketName,
            string objectName,
            TimeSpan expiry,
            bool requireAuth = true,
            CancellationToken cancellationToken = default); // ← Add CancellationToken

        Task<Stream> DownloadAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken = default); // ← Add CancellationToken

        Task DeleteAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken = default); // ← Add CancellationToken

        Task EnsureBucketExistsAsync(
            string bucketName,
            CancellationToken cancellationToken = default); // ← Add CancellationToken
    }

    public interface IFileValidator
    {
        Task<FileValidationResult> ValidateAsync(IFormFile file);
        Task<FileValidationResult> ValidateMetadataAsync(string fileName, string contentType, long totalSize);
    }

    public interface IFileSignatureValidator
    {
        Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream);
    }

    public interface IVirusScanner
    {
        Task<VirusScanResult> ScanAsync(Stream fileStream);
    }
}