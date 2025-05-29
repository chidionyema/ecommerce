// File: ecommerce.Services/Content/ChunkedUploadService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using ecommerce.Contracts;
using ecommerce.Dto;
// Ensure ecommerce.Db is referenced if Content and ContentType enum are there.
// using ecommerce.Db; // This using is not strictly needed if the alias below is used for ALL Db.ContentType needs
using ecommerce.Models; // If ChunkSession or other models are here (like your ContentType enum definition if it were here)
using Microsoft.AspNetCore.Http; // Not directly used but often relevant
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using ecommerce.Infrastructure; // Assuming IDistributedLockProvider is here
using System.Security.Cryptography;

// Alias for your specific ContentType enum from the Db namespace
using ContentType = ecommerce.Db.ContentType; // This assumes ecommerce.Db.ContentType is the enum you want to use

namespace ecommerce.Services
{
    public class ChunkedUploadService : IChunkedUploadService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IDistributedLockProvider _lockProvider;
        private readonly IContentStorageService _storageService;
        private readonly IVirusScanner _virusScanner;
        private readonly ILogger<ChunkedUploadService> _logger;

        public ChunkedUploadService(
            IConnectionMultiplexer redis,
            IDistributedLockProvider lockProvider,
            IContentStorageService storageService,
            IVirusScanner virusScanner,
            ILogger<ChunkedUploadService> logger)
        {
            _redis = redis ?? throw new ArgumentNullException(nameof(redis));
            _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _virusScanner = virusScanner ?? throw new ArgumentNullException(nameof(virusScanner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ChunkSession> InitSessionAsync(ChunkSessionRequest request)
        {
            _logger.LogInformation("Starting chunk session initialization for EntityId: {EntityId}, FileName: {FileName}", request.EntityId, request.FileName);

            var session = new ChunkSession // Assuming ecommerce.Models.ChunkSession
            {
                Id = Guid.NewGuid(),
                EntityId = request.EntityId,
                FileName = SanitizeFileName(request.FileName),
                TotalChunks = request.TotalChunks,
                TotalSize = request.TotalSize,
                ExpiresAt = DateTime.UtcNow.AddHours(6)
            };

            _logger.LogDebug("Initialized session details: {@Session}", session);

            var redisKey = $"chunkSession:{session.Id}";
            bool redisSetResult = await _redis.GetDatabase().StringSetAsync(
                redisKey,
                JsonSerializer.Serialize(session),
                TimeSpan.FromHours(6));

            _logger.LogInformation("Stored chunk session {SessionId} in Redis with key {RedisKey}. Set result: {Result}", session.Id, redisKey, redisSetResult);

            return session;
        }

        public async Task ProcessChunkAsync(Guid sessionId, int chunkIndex, Stream chunkData)
        {
            long initialChunkDataLength = -1;
            try { if (chunkData.CanSeek) initialChunkDataLength = chunkData.Length; } catch { /* ignore */ }
            _logger.LogInformation("Processing chunk {ChunkIndex} for session {SessionId}. Initial Stream Length (if available): {InitialChunkDataLength} bytes",
                chunkIndex, sessionId, initialChunkDataLength);

            var session = await GetSessionAsync(sessionId);

            await using var lockHandle = await _lockProvider.AcquireLockAsync(
                $"chunk:{sessionId}:{chunkIndex}",
                TimeSpan.FromSeconds(60));

            _logger.LogInformation("Validating chunk {ChunkIndex} for session {SessionId}", chunkIndex, sessionId);

            using var memoryStream = new MemoryStream();
            await chunkData.CopyToAsync(memoryStream);
            await memoryStream.FlushAsync();
            memoryStream.Position = 0;

            ValidateChunk(session, chunkIndex, memoryStream.Length);

            _logger.LogInformation("Prepared chunk {ChunkIndex} for upload. Size in memory: {MemoryStreamLength} bytes. SessionId: {SessionId}",
                chunkIndex, memoryStream.Length, sessionId);

            var chunkKey = $"{sessionId}/{chunkIndex}";
            _logger.LogInformation("Uploading chunk {ChunkIndex} for session {SessionId} to storage with key {ChunkKey}",
                chunkIndex, sessionId, chunkKey);

            memoryStream.Position = 0;
            var checksum = Convert.ToHexString(SHA256.HashData(memoryStream.ToArray()));
            _logger.LogDebug("Chunk checksum before upload: {Checksum}. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}", checksum, sessionId, chunkIndex);
            memoryStream.Position = 0;

            await _storageService.UploadAsync(
                memoryStream,
                "temp-chunks",
                chunkKey,
                "application/octet-stream",
                new Dictionary<string, string>
                {
                    ["sessionId"] = sessionId.ToString(),
                    ["chunkIndex"] = chunkIndex.ToString(),
                    ["checksum"] = checksum,
                    ["originalSize"] = memoryStream.Length.ToString()
                });

            _logger.LogInformation("Uploaded chunk {ChunkIndex} to {Bucket}/{ChunkKey}. SessionId: {SessionId}",
                chunkIndex, "temp-chunks", chunkKey, sessionId);

            session.UploadedChunks.Add(chunkIndex);
            await UpdateSessionAsync(session);
            _logger.LogInformation("Session {SessionId} updated in Redis after processing chunk {ChunkIndex}. Uploaded chunks count: {UploadedCount}",
                sessionId, chunkIndex, session.UploadedChunks.Count);
        }

        public async Task<ChunkSession> GetSessionAsync(Guid sessionId)
        {
            _logger.LogInformation("Retrieving chunk session {SessionId} from Redis", sessionId);
            var redisKey = $"chunkSession:{sessionId}";
            var raw = await _redis.GetDatabase().StringGetAsync(redisKey);
            if (raw.IsNullOrEmpty)
            {
                _logger.LogWarning("Chunk session {SessionId} not found in Redis with key {RedisKey}", sessionId, redisKey);
                throw new InvalidOperationException($"Chunk session {sessionId} not found.");
            }

            var session = JsonSerializer.Deserialize<ChunkSession>(
                raw.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (session == null)
            {
                _logger.LogError("Deserialization of chunk session {SessionId} (key: {RedisKey}) returned null. Raw data: {RawData}", sessionId, redisKey, raw.ToString());
                throw new InvalidOperationException($"Failed to deserialize chunk session {sessionId}.");
            }

            _logger.LogInformation("Successfully retrieved and deserialized chunk session {SessionId} from Redis", sessionId);
            return session;
        }

        public async Task<ecommerce.Db.Content> CompleteSessionAsync(Guid sessionId, string userId)
        {
            _logger.LogInformation("Initiating completion of chunk session {SessionId} for user {UserId}", sessionId, userId);

            await using var lockHandle = await _lockProvider.AcquireLockAsync(
                $"completeSession:{sessionId}",
                TimeSpan.FromMinutes(15));
            _logger.LogDebug("Acquired completion lock for session {SessionId}", sessionId);

            var session = await GetSessionAsync(sessionId);
            _logger.LogInformation("Validating session completion prerequisites for session {SessionId}", sessionId);
            ValidateSessionCompletion(session);

            _logger.LogInformation("Assembling chunks for session {SessionId}. Expected TotalSize: {TotalSize}, TotalChunks: {TotalChunks}",
                sessionId, session.TotalSize, session.TotalChunks);
            var tempFilePath = await AssembleChunksAsync(session);

            _logger.LogInformation("Validating final assembled file at {TempFilePath} for session {SessionId}", tempFilePath, sessionId);
            await ValidateFinalFileAsync(tempFilePath);

            _logger.LogInformation("Storing final assembled file from {TempFilePath} for session {SessionId} to permanent storage", tempFilePath, sessionId);
            var finalContent = await StoreFinalFileAsync(tempFilePath, session, userId); // Returns ecommerce.Db.Content
            _logger.LogInformation("Final file stored successfully. ContentId: {ContentId}, Path: {Path}, for session {SessionId}",
                finalContent.Id, finalContent.Path, sessionId);

            // Assuming the caller of CompleteSessionAsync (e.g., ContentController) is responsible for
            // adding finalContent to the IContentRepository and saving changes for the Content entity.

            _logger.LogInformation("Cleaning up temporary resources (chunks, temp file, Redis session) for session {SessionId}", sessionId);
            await CleanupResourcesAsync(session, tempFilePath);

            _logger.LogInformation("Chunk session {SessionId} completed successfully for user {UserId}. Final ContentId: {ContentId}",
                sessionId, userId, finalContent.Id);
            return finalContent;
        }

        private async Task ValidateFinalFileAsync(string tempFilePath)
        {
            _logger.LogInformation("Scanning assembled file for viruses: {TempFilePath}", tempFilePath);
            await using var fileStream = File.OpenRead(tempFilePath);
            var scanResult = await _virusScanner.ScanAsync(fileStream);
            if (scanResult.IsMalicious)
            {
                _logger.LogError("Virus scan detected malicious content in assembled file: {TempFilePath}. Threat: {ThreatName}", tempFilePath, scanResult.ThreatName);
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete malicious temp file {TempFilePath} after scan.", tempFilePath); }
                throw new SecurityException($"Final assembled file '{Path.GetFileName(tempFilePath)}' contains malicious content: {scanResult.ThreatName}");
            }
            _logger.LogInformation("Virus scan passed for assembled file: {TempFilePath}", tempFilePath);
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogWarning("SanitizeFileName: Input fileName was null or whitespace, returning 'default_filename'.");
                return "default_filename";
            }
            _logger.LogInformation("Sanitizing file name: Original='{OriginalFileName}'", fileName);
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitizedName = new string(fileName
                .Replace(" ", "_")
                .Where(c => !invalidChars.Contains(c) && c < 128)
                .DefaultIfEmpty('_')
                .ToArray());

            if (sanitizedName.Length > 200)
            {
                var ext = Path.GetExtension(sanitizedName);
                sanitizedName = sanitizedName.Substring(0, 200 - (ext?.Length ?? 0)) + ext;
            }
            _logger.LogInformation("Sanitized file name: Result='{SanitizedFileName}'", sanitizedName);
            return sanitizedName;
        }

        private async Task UpdateSessionAsync(ChunkSession session)
        {
            _logger.LogInformation("Updating chunk session {SessionId} in Redis. Uploaded chunks count: {UploadedCount}", session.Id, session.UploadedChunks.Count);
            var redisKey = $"chunkSession:{session.Id}";
            await _redis.GetDatabase().StringSetAsync(redisKey, JsonSerializer.Serialize(session), TimeSpan.FromHours(6));
            _logger.LogInformation("Chunk session {SessionId} updated successfully in Redis", session.Id);
        }

        private void ValidateChunk(ChunkSession session, int chunkIndex, long actualChunkSize)
        {
            _logger.LogInformation("Validating chunk {ChunkIndex} with actual size {ActualChunkSize} for session {SessionId} (TotalSize: {TotalSize}, TotalChunks: {TotalChunks})",
                chunkIndex, actualChunkSize, session.Id, session.TotalSize, session.TotalChunks);

            if (chunkIndex < 0 || chunkIndex >= session.TotalChunks)
            {
                _logger.LogError("Invalid chunk index {ChunkIndex} for session {SessionId}. Must be between 0 and {MaxChunkIndex}.",
                    chunkIndex, session.Id, session.TotalChunks - 1);
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), $"Invalid chunk index {chunkIndex}. Must be between 0 and {session.TotalChunks - 1}.");
            }

            if (actualChunkSize <= 0)
            {
                if (session.TotalSize > 0) 
                {
                    _logger.LogError("Chunk size {ActualChunkSize} is not greater than zero for session {SessionId}, chunk {ChunkIndex}, while TotalSize is {TotalSize}",
                        actualChunkSize, session.Id, chunkIndex, session.TotalSize);
                    throw new ArgumentException("Chunk size must be greater than zero if total file size is greater than zero.", nameof(actualChunkSize));
                }
                else if (session.TotalSize == 0 && session.TotalChunks == 1 && actualChunkSize == 0)
                {
                     _logger.LogInformation("Allowing 0-byte chunk for 0-byte total file upload. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}", session.Id, chunkIndex);
                }
                else 
                {
                    _logger.LogError("Invalid 0-byte chunk scenario. ActualChunkSize: {ActualChunkSize}, TotalSize: {TotalSize}, TotalChunks: {TotalChunks}. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}",
                        actualChunkSize, session.TotalSize, session.TotalChunks, session.Id, chunkIndex);
                    throw new ArgumentException("Invalid chunk size configuration for 0-byte file.", nameof(actualChunkSize));
                }
            }
            _logger.LogInformation("Chunk {ChunkIndex} (size: {ActualChunkSize}) validated successfully for session {SessionId}",
                chunkIndex, actualChunkSize, session.Id);
        }


        private void ValidateSessionCompletion(ChunkSession session)
        {
            _logger.LogInformation("Validating session completion for session {SessionId}. Uploaded chunks: {UploadedChunksCount}/{TotalChunks}",
                session.Id, session.UploadedChunks.Count, session.TotalChunks);

            if (session.UploadedChunks.Count != session.TotalChunks)
            {
                _logger.LogError("Session {SessionId} is incomplete. Expected {TotalChunks} chunks, but received {UploadedChunksCount}. Missing chunks: {@MissingIndexes}",
                    session.Id, session.TotalChunks, session.UploadedChunks.Count,
                    Enumerable.Range(0, session.TotalChunks).Except(session.UploadedChunks).ToList());
                throw new InvalidOperationException($"Session {session.Id} incomplete. Received {session.UploadedChunks.Count}/{session.TotalChunks} chunks.");
            }

            if (DateTime.UtcNow > session.ExpiresAt)
            {
                _logger.LogError("Session {SessionId} has expired. ExpiredAt: {ExpiresAt}, CurrentTime: {CurrentTime}",
                    session.Id, session.ExpiresAt, DateTime.UtcNow);
                throw new TimeoutException($"Session {session.Id} has expired.");
            }
            _logger.LogInformation("Session {SessionId} passed completion validation (all chunks received, not expired)", session.Id);
        }

        // THIS IS THE UPDATED METHOD
        private async Task<string> AssembleChunksAsync(ChunkSession session)
        {
            _logger.LogInformation("Starting assembly of chunks for session {SessionId}. Total Chunks: {TotalChunks}, Expected Total Size: {TotalSize}",
                session.Id, session.TotalChunks, session.TotalSize);

            var tempDir = Path.Combine(Path.GetTempPath(), "chunk_assemblies_ecommerce"); 
            Directory.CreateDirectory(tempDir);
            _logger.LogDebug("Temporary directory for chunk assemblies: {TempDir}", tempDir);

            var tempFile = Path.Combine(tempDir, $"{session.Id}_{SanitizeFileName(session.FileName)}_assembled"); 
            _logger.LogInformation("Assembled file will be created at: {TempFile}", tempFile);

            long totalBytesWrittenToOutputFile = 0;

            try
            {
                await using (var outputStream = File.Create(tempFile))
                {
                    for (int i = 0; i < session.TotalChunks; i++) // 'i' is the current chunk index
                    {
                        var chunkKey = $"{session.Id}/{i}";
                        _logger.LogInformation("[Assemble] Attempting to download chunk {ChunkIndex}/{TotalChunksIndex} (Key: {ChunkKey}) from bucket 'temp-chunks' for session {SessionId}",
                            i, session.TotalChunks - 1, chunkKey, session.Id);

                        Stream? chunkStream = null;
                        try
                        {
                            chunkStream = await _storageService.DownloadAsync("temp-chunks", chunkKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Assemble] Failed to download chunk {ChunkIndex} (Key: {ChunkKey}) for session {SessionId}. Aborting assembly.", i, chunkKey, session.Id);
                            throw new InvalidOperationException($"Failed to download chunk {i} for session {session.Id}. Assembly aborted.", ex);
                        }

                        if (chunkStream == null)
                        {
                            _logger.LogError("[Assemble] Downloaded chunk stream for chunk {ChunkIndex} (Key: {ChunkKey}) is NULL. Session {SessionId}. Aborting assembly.", i, chunkKey, session.Id);
                            throw new InvalidOperationException($"Downloaded stream for chunk {i} (session {session.Id}) was null. Assembly aborted.");
                        }

                        await using (chunkStream) 
                        {
                            long downloadedChunkLength = -1;
                            string chunkContentPreview = "N/A (cannot read or empty/unseekable)";

                            if (!chunkStream.CanRead)
                            {
                                _logger.LogError("[Assemble] Critical: Chunk {ChunkIndex} (Key: {ChunkKey}) for session {SessionId} is NOT READABLE. Aborting assembly.", 
                                    i, chunkKey, session.Id);
                                throw new InvalidDataException($"Chunk {i} (session {session.Id}) stream is not readable.");
                            }

                            if (chunkStream.CanSeek)
                            {
                                downloadedChunkLength = chunkStream.Length;
                                chunkStream.Position = 0; 
                                if (downloadedChunkLength > 0)
                                {
                                    byte[] buffer = new byte[Math.Min(16, (int)downloadedChunkLength)];
                                    int bytesRead = await chunkStream.ReadAsync(buffer, 0, buffer.Length);
                                    chunkContentPreview = BitConverter.ToString(buffer, 0, bytesRead);
                                    chunkStream.Position = 0; 
                                }
                                else
                                {
                                    chunkContentPreview = "Stream is empty (0 length reported by seekable stream)";
                                }
                            }
                            else
                            {
                                _logger.LogWarning("[Assemble] Chunk stream for {ChunkKey} is not seekable. Cannot determine length before copy or provide accurate preview.", chunkKey);
                            }
                            
                            _logger.LogInformation("[Assemble] Downloaded chunk {ChunkIndex} (Key: {ChunkKey}). Seekable: {IsSeekable}, Reported Length (if seekable): {Length}. Preview: {Preview}. Stream CanRead: {CanRead}",
                                i, chunkKey, chunkStream.CanSeek, downloadedChunkLength, chunkContentPreview, chunkStream.CanRead);
                            
                            // If the stream is seekable, has a reported length of 0, AND we expect the total file to have content,
                            // then this specific chunk being empty is an error.
                            if (chunkStream.CanSeek && downloadedChunkLength == 0 && session.TotalSize > 0)
                            {
                                 _logger.LogError("[Assemble] Critical: Chunk {ChunkIndex} for session {SessionId} is definitively empty (seekable, length 0), but TotalSize is {TotalSessionSize}. Aborting assembly.",
                                    i, session.Id, session.TotalSize);
                                 throw new InvalidDataException($"Chunk {i} for session {session.Id} downloaded as empty (length: 0), but data was expected as TotalSize is {session.TotalSize}.");
                            }
                            
                            _logger.LogInformation("[Assemble] Copying chunk {ChunkIndex} into assembled file for session {SessionId}. Output stream position before copy: {Position}", i, session.Id, outputStream.Position);
                            await chunkStream.CopyToAsync(outputStream);
                            await outputStream.FlushAsync(); 
                            totalBytesWrittenToOutputFile = outputStream.Position; 
                            _logger.LogInformation("[Assemble] Chunk {ChunkIndex} copy complete. Assembled output stream current total length: {OutputLength}", i, totalBytesWrittenToOutputFile);
                        } 
                    }
                } 
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "[Assemble] Exception during chunk assembly loop or File.Create for session {SessionId}. Temp file: {TempFile}", session.Id, tempFile);
                try { if(File.Exists(tempFile)) File.Delete(tempFile); } catch (IOException ioEx) { _logger.LogWarning(ioEx, "Failed to delete partially assembled temp file {TempFile} after an error.", tempFile); }
                throw; 
            }

            var fileInfo = new FileInfo(tempFile); 
            _logger.LogInformation("Assembly process finished for session {SessionId}. Final assembled file size on disk: {ActualFileSize} bytes. Expected total size from session: {ExpectedTotalSize} bytes. Total bytes written to stream: {TotalBytesWritten}",
                session.Id, fileInfo.Length, session.TotalSize, totalBytesWrittenToOutputFile);
            
            if (fileInfo.Length != totalBytesWrittenToOutputFile) {
                _logger.LogWarning("[Assemble] Mismatch between final FileInfo.Length ({FileInfoLength}) and sum of bytes tracked during copy ({TotalBytesCopied}) for session {SessionId}. This is highly unusual and indicates a discrepancy in file I/O.", 
                fileInfo.Length, totalBytesWrittenToOutputFile, session.Id);
            }

            if (fileInfo.Length != session.TotalSize)
            {
                _logger.LogError("Assembled file size mismatch for session {SessionId}. Actual on disk: {ActualSize}, Expected: {ExpectedSize}. Cleaning up temp file: {TempFile}",
                    session.Id, fileInfo.Length, session.TotalSize, tempFile);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (IOException ioEx) { _logger.LogWarning(ioEx, "Failed to delete mismatched temp file {TempFile} after size check.", tempFile); }
                throw new InvalidDataException($"Assembled file size {fileInfo.Length} doesn't match expected {session.TotalSize} for session {session.Id}.");
            }

            _logger.LogInformation("All chunks assembled successfully for session {SessionId}. File: {TempFile}, Size: {ActualFileSize}",
                session.Id, tempFile, fileInfo.Length);
            return tempFile;
        }

        private async Task<ecommerce.Db.Content> StoreFinalFileAsync(string tempFilePath, ChunkSession session, string userId)
        {
             _logger.LogInformation("Storing final file from {TempFilePath} for session {SessionId}, User {UserId}, OriginalFileName {FileName}",
                tempFilePath, session.Id, userId, session.FileName);

            var fileExtension = Path.GetExtension(session.FileName);
            ContentType contentTypeEnum = GetContentType(fileExtension); // Uses alias
            string mimeType = GetMimeType(contentTypeEnum); // Uses alias

            var objectNameSuffix = SanitizeFileName(Path.GetFileNameWithoutExtension(session.FileName)) + fileExtension;
            var finalObjectName = $"{userId}/{session.EntityId}/{session.Id}_{objectNameSuffix}";
            if (finalObjectName.Length > 900) finalObjectName = finalObjectName.Substring(0, 900) + fileExtension;


            _logger.LogInformation("Final object details for session {SessionId}: Name='{FinalObjectName}', MimeType='{MimeType}', Bucket='final-content'",
                session.Id, finalObjectName, mimeType);

            ContentUploadResult uploadResult;
            try
            {
                await using var fileStream = File.OpenRead(tempFilePath);
                 _logger.LogDebug("Opened final temp file {TempFilePath} for upload. Stream Length: {StreamLength}", tempFilePath, fileStream.Length);
                uploadResult = await _storageService.UploadAsync(
                    fileStream,
                    "final-content",
                    finalObjectName,
                    mimeType,
                    new Dictionary<string, string>
                    {
                        ["entityId"] = session.EntityId.ToString(),
                        ["userId"] = userId,
                        ["originalFileName"] = session.FileName,
                        ["uploadSessionId"] = session.Id.ToString()
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload final assembled file {TempFilePath} to permanent storage for session {SessionId}. ObjectName attempt: {FinalObjectName}",
                    tempFilePath, session.Id, finalObjectName);
                throw;
            }

            _logger.LogInformation("Final file for session {SessionId} uploaded to permanent storage. Bucket: {BucketName}, ObjectName: {ObjectName}, Path: {Path}, VersionId/ETag: {VersionId}",
                session.Id, uploadResult.BucketName, uploadResult.ObjectName, uploadResult.Path, uploadResult.VersionId);

            var content = new ecommerce.Db.Content // Ensure this is the correct Content entity type
            {
                Id = Guid.NewGuid(),
                EntityId = session.EntityId,
                EntityType = "ChunkUploadedContent",
                FileName = session.FileName,
                ContentType = contentTypeEnum, // This is now correctly ecommerce.Db.ContentType via the alias
                FileSize = session.TotalSize,
                BucketName = uploadResult.BucketName,
                ObjectName = uploadResult.ObjectName,
                Path = uploadResult.Path,
                ETag = uploadResult.VersionId,
                StorageDetails = uploadResult.StorageDetails,
                CreatedAt = DateTime.UtcNow
            };

            _logger.LogInformation("Content database record prepared for ContentId {ContentId}, SessionId {SessionId}", content.Id, session.Id);
            return content;
        }

        private async Task CleanupResourcesAsync(ChunkSession session, string tempFilePath)
        {
            _logger.LogInformation("Cleaning up temporary resources for session {SessionId}. Temp assembled file: {TempFilePath}", session.Id, tempFilePath);

            _logger.LogDebug("Attempting to delete {TotalChunks} individual chunks from 'temp-chunks' for session {SessionId}", session.TotalChunks, session.Id);
            List<Task> deleteTasks = new List<Task>();
            for (int i = 0; i < session.TotalChunks; i++)
            {
                var chunkKey = $"{session.Id}/{i}";
                deleteTasks.Add(_storageService.DeleteAsync("temp-chunks", chunkKey)
                    .ContinueWith(t => {
                        if (t.IsFaulted) _logger.LogWarning(t.Exception?.Flatten(), "Failed to delete chunk {ChunkKey} for session {SessionId} during cleanup.", chunkKey, session.Id);
                        else _logger.LogDebug("Successfully deleted chunk {ChunkKey} for session {SessionId}", chunkKey, session.Id);
                    }));
            }
            try
            {
                await Task.WhenAll(deleteTasks);
                _logger.LogInformation("All chunk deletion tasks completed for session {SessionId}.", session.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "One or more chunk deletion tasks encountered issues for session {SessionId}.", session.Id);
            }

            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                    _logger.LogInformation("Successfully deleted temporary assembled file: {TempFilePath} for session {SessionId}", tempFilePath, session.Id);
                }
                else
                {
                    _logger.LogWarning("Temporary assembled file not found for deletion: {TempFilePath} for session {SessionId}", tempFilePath, session.Id);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting temporary assembled file {TempFilePath} for session {SessionId}", tempFilePath, session.Id);
            }

            var redisKey = $"chunkSession:{session.Id}";
            _logger.LogDebug("Attempting to delete Redis key {RedisKey} for session {SessionId}", redisKey, session.Id);
            try
            {
                if (await _redis.GetDatabase().KeyDeleteAsync(redisKey))
                {
                    _logger.LogInformation("Successfully deleted Redis key {RedisKey} for session {SessionId}", redisKey, session.Id);
                }
                else
                {
                    _logger.LogWarning("Redis key {RedisKey} not found for deletion (or already deleted) for session {SessionId}", redisKey, session.Id);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Error deleting Redis key {RedisKey} for session {SessionId}", redisKey, session.Id);
            }

            _logger.LogInformation("Cleanup of temporary resources completed for session {SessionId}", session.Id);
        }

        private ContentType GetContentType(string fileExtension) // Return type uses the alias
        {
            string ext = string.IsNullOrWhiteSpace(fileExtension) ? "" : fileExtension.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && !ext.StartsWith("."))
            {
                ext = "." + ext;
            }
            _logger.LogDebug("Determining ContentType for normalized file extension '{NormalizedFileExtension}'", ext);
            
            var determinedContentType = ext switch
            {
                ".jpg" or ".jpeg" => ContentType.Image,
                ".png" => ContentType.Image,
                ".gif" => ContentType.Image,
                ".webp" => ContentType.Image, // Assuming you have WebP in your enum
                ".pdf" => ContentType.Document,
                ".doc" or ".docx" => ContentType.Document,
                ".xls" or ".xlsx" => ContentType.Document,
                ".ppt" or ".pptx" => ContentType.Document,
                ".txt" => ContentType.Document,
                ".mp4" => ContentType.Video,
                ".mov" => ContentType.Video,
                ".avi" => ContentType.Video,
                ".wmv" => ContentType.Video,
                // ".mp3" => ContentType.Audio, // Based on your enum, Audio is not a member
                // ".wav" => ContentType.Audio, // Based on your enum, Audio is not a member
                _ => ContentType.Other
            };
            _logger.LogInformation("Determined ContentType: {ContentType} for file extension '{NormalizedFileExtension}'", determinedContentType, ext);
            return determinedContentType;
        }

        private string GetMimeType(ContentType contentType) // Parameter uses the alias
        {
            _logger.LogDebug("Determining MIME type for ContentType {ContentType}", contentType);
            var mimeType = contentType switch
            {
                ContentType.Image => "image/jpeg",
                ContentType.Document => "application/pdf",
                ContentType.Video => "video/mp4",
                // ContentType.Audio => "audio/mpeg", // Based on your enum, Audio is not a member
                _ => "application/octet-stream"
            };
            _logger.LogInformation("Determined MIME type: '{MimeType}' for ContentType {ContentType}", mimeType, contentType);
            return mimeType;
        }
    }
}