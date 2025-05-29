using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

// ASP.NET Core Imports
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// Project-specific Imports
using ecommerce.Contracts;
using ecommerce.Models;
using ecommerce.Dto;
using ecommerce.Db;
using ecommerce.Extensions;
using ecommerce.Infrastructure.Repository.Interfaces;

// Additional System Imports
using System.Security;

namespace ecommerce.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize(Policy = "ContentUploader")]
    public class ContentController : ControllerBase
    {
        private readonly IContentStorageService _storageService;
        private readonly IFileValidator _fileValidator;
        private readonly IChunkedUploadService _chunkedService;
        private readonly IContentRepository _contentRepository;
        private readonly ILogger<ContentController> _logger;
        
        // Add ActivitySource for proper distributed tracing
        private static readonly ActivitySource ActivitySource = new("ContentController");

        public ContentController(
            IContentStorageService storageService,
            IFileValidator fileValidator,
            IChunkedUploadService chunkedService,
            IContentRepository contentRepository,
            ILogger<ContentController> logger)
        {
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _fileValidator = fileValidator ?? throw new ArgumentNullException(nameof(fileValidator));
            _chunkedService = chunkedService ?? throw new ArgumentNullException(nameof(chunkedService));
            _contentRepository = contentRepository ?? throw new ArgumentNullException(nameof(contentRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost("upload")]
        [RequestSizeLimit(100_000_000)]
        [ProducesResponseType(typeof(ContentDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UploadFile(
            [FromQuery] Guid entityId,
            [FromForm] IFormFile file)
        {
            using var activity = ActivitySource.StartActivity("UploadFile");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                if (file == null || file.Length == 0)
                {
                    _logger.LogWarning("UploadFile: No file provided or file is empty.");
                    return BadRequest(new { error = "No file provided or file is empty." });
                }

                _logger.LogInformation(
                    "Starting file upload. EntityId: {EntityId}, FileName: {FileName}, Size: {FileSize}, ContentType: {FileContentType}", 
                    entityId, file.FileName, file.Length, file.ContentType);

                // Validate file first (before opening stream)
                var validationResult = await _fileValidator.ValidateAsync(file);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "File validation failed for {FileName}. Errors: {ValidationErrors}", 
                        file.FileName, string.Join(", ", validationResult.Errors));
                    return BadRequest(new { errors = validationResult.Errors });
                }

                string userId = User.GetUserId() ?? "unknown_user";
                ContentUploadResult uploadResult;

                // CRITICAL: Properly scope the file stream
                try 
                {
                    await using var fileStream = file.OpenReadStream();
                    
                    uploadResult = await _storageService.UploadAsync(
                        fileStream,
                        GetBucketForType(validationResult.FileType),
                        GenerateObjectName(file.FileName, userId),
                        file.ContentType,
                        GetSecurityTags(validationResult.FileType)
                    );
                    
                    _logger.LogInformation(
                        "File uploaded successfully to storage. Bucket: {Bucket}, ObjectName: {ObjectName}, Path: {Path}", 
                        uploadResult.BucketName, uploadResult.ObjectName, uploadResult.Path);
                }
                catch (Exception storageEx)
                {
                    _logger.LogError(storageEx, 
                        "Storage upload failed for {FileName}. Bucket: {Bucket}", 
                        file.FileName, GetBucketForType(validationResult.FileType));
                    return StatusCode(StatusCodes.Status500InternalServerError, 
                        new { error = "Failed to upload file to storage." });
                }

                ContentType parsedContentType = ParseContentType(file.ContentType);

                var content = new Content
                {
                    Id = Guid.NewGuid(),
                    EntityId = entityId,
                    EntityType = GetBucketForType(validationResult.FileType),
                    FileName = file.FileName,
                    ContentType = parsedContentType,
                    BucketName = uploadResult.BucketName,
                    ObjectName = uploadResult.ObjectName,
                    ETag = uploadResult.VersionId,
                    FileSize = file.Length,
                    StorageDetails = uploadResult.StorageDetails,
                    Path = uploadResult.Path,
                };

                try 
                {
                    await _contentRepository.AddContentsAsync(new[] { content });
                    _logger.LogInformation(
                        "Content record added to database. ContentId: {ContentId}, Duration: {Duration}ms", 
                        content.Id, stopwatch.ElapsedMilliseconds);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, 
                        "Database insertion failed for content related to file {FileName}, ContentId attempt: {ContentId}", 
                        file.FileName, content.Id);
                    
                    // CRITICAL: Cleanup uploaded file on DB failure
                    try
                    {
                        await _storageService.DeleteAsync(uploadResult.BucketName, uploadResult.ObjectName);
                        _logger.LogInformation("Cleaned up uploaded file after DB failure");
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogError(cleanupEx, "Failed to cleanup uploaded file after DB failure");
                    }
                    
                    return StatusCode(StatusCodes.Status500InternalServerError, 
                        new { error = "Failed to save content metadata to database." });
                }

                return CreatedAtAction(nameof(GetContent),
                    new { id = content.Id },
                    MapToDto(content));
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, 
                    "Unexpected error during file upload. EntityId: {EntityId}, FileName: {FileName}, Duration: {Duration}ms", 
                    entityId, file?.FileName ?? "N/A", stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, 
                    new { error = "An unexpected error occurred during file upload." });
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpGet("{id:guid}")]
        [ProducesResponseType(typeof(ContentDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetContent(Guid id)
        {
            using var activity = ActivitySource.StartActivity("GetContent");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation("Retrieving content metadata. ContentId: {ContentId}", id);

                Content? content = await _contentRepository.GetContentByIdAsync(id);
                
                if (content == null)
                {
                    _logger.LogWarning(
                        "Content metadata not found. ContentId: {ContentId}, Duration: {Duration}ms", 
                        id, stopwatch.ElapsedMilliseconds);
                    return NotFound(new { error = $"Content with ID {id} not found." });
                }

                _logger.LogInformation(
                    "Content metadata retrieved successfully. ContentId: {ContentId}, Duration: {Duration}ms", 
                    id, stopwatch.ElapsedMilliseconds);
                
                return Ok(MapToDto(content));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error retrieving content metadata. ContentId: {ContentId}, Duration: {Duration}ms", 
                    id, stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, 
                    new { error = "An error occurred while retrieving the content metadata." });
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]  
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteContent(Guid id)
        {
            using var activity = ActivitySource.StartActivity("DeleteContent");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation("Attempting to delete content. ContentId: {ContentId}", id);

                var content = await _contentRepository.GetContentByIdAsync(id);
                
                if (content == null)
                {
                    _logger.LogWarning(
                        "Content not found for deletion. ContentId: {ContentId}, Duration: {Duration}ms", 
                        id, stopwatch.ElapsedMilliseconds);
                    return NotFound(new { error = $"Content with ID {id} not found." });
                }

                // CRITICAL: Delete from storage first, then DB
                try
                {
                    await _storageService.DeleteAsync(content.BucketName, content.ObjectName);
                    _logger.LogInformation("Successfully deleted file from storage: Bucket {BucketName}, Object {ObjectName}", 
                        content.BucketName, content.ObjectName);
                }
                catch (Exception storageEx)
                {
                    _logger.LogError(storageEx, "Failed to delete file from storage for ContentId {ContentId}. Proceeding with DB deletion.", id);
                    // Continue with DB deletion even if storage fails
                }

                await _contentRepository.RemoveContentAsync(content);
                
                _logger.LogInformation(
                    "Content record deleted successfully from database. ContentId: {ContentId}, Duration: {Duration}ms", 
                    id, stopwatch.ElapsedMilliseconds);
                
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error deleting content. ContentId: {ContentId}, Duration: {Duration}ms", 
                    id, stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, 
                    new { error = "An error occurred while deleting the content." });
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpPost("chunked/init")]
        [RequestSizeLimit(10_000)]
        [ProducesResponseType(typeof(ChunkSession), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> InitChunkSession([FromBody] ChunkSessionRequest request)
        {
            using var activity = ActivitySource.StartActivity("InitChunkSession");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation(
                    "Initializing chunk session. EntityId: {EntityId}, FileName: {FileName}, TotalSize: {TotalSize}, ChunkSize: {ChunkSize}", 
                    request.EntityId, request.FileName, request.TotalSize, request.ChunkSize);

                if (!ModelState.IsValid)
                {
                    _logger.LogWarning(
                        "Invalid chunk session request. Errors: {ModelErrors}", 
                        string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
                    return BadRequest(ModelState);
                }

                // CRITICAL: Validate request parameters
                if (request.TotalChunks <= 0 || request.TotalSize <= 0 || request.ChunkSize <= 0)
                {
                    _logger.LogWarning("Invalid chunk session parameters: TotalChunks={TotalChunks}, TotalSize={TotalSize}, ChunkSize={ChunkSize}",
                        request.TotalChunks, request.TotalSize, request.ChunkSize);
                    return BadRequest(new { error = "Invalid chunk session parameters." });
                }

                var validationResult = await _fileValidator.ValidateMetadataAsync(request.FileName, request.ContentType, request.TotalSize);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "File metadata validation failed for chunked upload {FileName}. Errors: {ValidationErrors}", 
                        request.FileName, string.Join(", ", validationResult.Errors));
                    return BadRequest(new { errors = validationResult.Errors });
                }

                var session = await _chunkedService.InitSessionAsync(request);
                
                _logger.LogInformation(
                    "Chunk session initialized. SessionId: {SessionId}, ExpectedChunks: {ExpectedChunks}, Duration: {Duration}ms", 
                    session.Id, session.TotalChunks, stopwatch.ElapsedMilliseconds);
                
                return CreatedAtAction(
                    nameof(GetChunkSessionStatus), 
                    new { sessionId = session.Id }, 
                    session
                );
            }
            catch (ArgumentException argEx)
            {
                 _logger.LogWarning(argEx, "Argument error initializing chunk session. EntityId: {EntityId}", request.EntityId);
                return BadRequest(new { error = argEx.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error initializing chunk session. EntityId: {EntityId}, FileName: {FileName}, Duration: {Duration}ms", 
                    request.EntityId, request.FileName, stopwatch.ElapsedMilliseconds);
                
                return StatusCode(
                    StatusCodes.Status500InternalServerError, 
                    new { error = "Failed to initialize chunk session." }
                );
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpPost("chunked/{sessionId}/{chunkIndex}")]
        [RequestSizeLimit(11_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 11_000_000)]
        public async Task<IActionResult> UploadChunk(
            Guid sessionId, 
            int chunkIndex, 
            IFormFile chunkFile)
        {
            using var activity = ActivitySource.StartActivity("UploadChunk");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation(
                    "Uploading chunk. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}, FilePresent: {FilePresent}, FileLength: {FileLength}", 
                    sessionId, chunkIndex, chunkFile != null, chunkFile?.Length ?? 0);

                if (chunkFile == null || chunkFile.Length == 0)
                {
                     _logger.LogWarning("UploadChunk: Invalid chunk file for SessionId: {SessionId}, ChunkIndex: {ChunkIndex}.", sessionId, chunkIndex);
                    return BadRequest(new { error = "Invalid or empty chunk file provided." });
                }

                // CRITICAL: Validate chunk index
                if (chunkIndex < 0)
                {
                    _logger.LogWarning("Invalid chunk index {ChunkIndex} for SessionId: {SessionId}", chunkIndex, sessionId);
                    return BadRequest(new { error = "Chunk index cannot be negative." });
                }
                
                // CRITICAL: Properly scope the chunk stream
                try
                {
                    await using var stream = chunkFile.OpenReadStream();
                    
                    await _chunkedService.ProcessChunkAsync(
                        sessionId, 
                        chunkIndex, 
                        stream 
                    );
                }
                catch (ObjectDisposedException ex)
                {
                    _logger.LogError(ex, "Stream was disposed during chunk processing for SessionId: {SessionId}, ChunkIndex: {ChunkIndex}", sessionId, chunkIndex);
                    return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Stream processing error." });
                }

                _logger.LogInformation(
                    "Chunk uploaded successfully. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}, Size: {Bytes} bytes, Duration: {Duration}ms", 
                    sessionId, chunkIndex, chunkFile.Length, stopwatch.ElapsedMilliseconds);
                
                return Ok(new { message = $"Chunk {chunkIndex} for session {sessionId} uploaded successfully." });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.LogWarning(ex, "Invalid chunk index for SessionId: {SessionId}, ChunkIndex: {ChunkIndex}.", sessionId, chunkIndex);
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Invalid operation during chunk upload for SessionId: {SessionId}, ChunkIndex: {ChunkIndex}.", sessionId, chunkIndex);
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chunk processing error for SessionId: {SessionId}, ChunkIndex: {ChunkIndex}.", sessionId, chunkIndex);
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to process chunk." });
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpPost("chunked/complete/{sessionId}")]
        [ProducesResponseType(typeof(ContentDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CompleteChunkSession(Guid sessionId)
        {
            using var activity = ActivitySource.StartActivity("CompleteChunkSession");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation(
                    "Attempting to complete chunk session. SessionId: {SessionId}", 
                    sessionId);

                string userId = User.GetUserId() ?? "unknown_user";
                
                // CRITICAL: Add timeout for session completion
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                
                var content = await _chunkedService.CompleteSessionAsync(sessionId, userId);
                
                if (content == null)
                {
                     _logger.LogError("CompleteSessionAsync returned null for SessionId: {SessionId}. This indicates an issue in the chunked service logic.", sessionId);
                    return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to finalize uploaded content." });
                }
                
                _logger.LogInformation(
                    "Chunk session completed and content record created. SessionId: {SessionId}, ContentId: {ContentId}, Duration: {Duration}ms", 
                    sessionId, content.Id, stopwatch.ElapsedMilliseconds);
                
                return CreatedAtAction(
                    nameof(GetContent), 
                    new { id = content.Id }, 
                    MapToDto(content)
                );
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, 
                    "Invalid operation completing chunk session. SessionId: {SessionId}", 
                    sessionId);
                return BadRequest(new { error = ex.Message });
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, 
                    "Chunk session completion timeout. SessionId: {SessionId}", 
                    sessionId);
                return StatusCode(StatusCodes.Status408RequestTimeout, new { error = "Session completion timed out." });
            }
            catch (SecurityException ex)
            {
                _logger.LogWarning(ex, 
                    "Security exception during chunk session completion. SessionId: {SessionId}", 
                    sessionId);
                return Forbid();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error completing chunk session. SessionId: {SessionId}, Duration: {Duration}ms", 
                    sessionId, stopwatch.ElapsedMilliseconds);
                
                return StatusCode(
                    StatusCodes.Status500InternalServerError, 
                    new { error = "Failed to complete chunk session." }
                );
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        [HttpGet("chunked/session/{sessionId}")]
        [ProducesResponseType(typeof(ChunkSession), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetChunkSessionStatus(Guid sessionId)
        {
            using var activity = ActivitySource.StartActivity("GetChunkSessionStatus");
            var stopwatch = Stopwatch.StartNew();
            
            try 
            {
                _logger.LogInformation(
                    "Retrieving chunk session status. SessionId: {SessionId}", 
                    sessionId);

                var session = await _chunkedService.GetSessionAsync(sessionId);
                
                _logger.LogInformation(
                    "Chunk session status retrieved. SessionId: {SessionId}, IsCompleted: {IsCompleted}, Duration: {Duration}ms", 
                    sessionId, session.IsCompleted, stopwatch.ElapsedMilliseconds);
                
                return Ok(session);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "Chunk session not found when retrieving status. SessionId: {SessionId}, Duration: {Duration}ms", 
                    sessionId, stopwatch.ElapsedMilliseconds);
                
                return NotFound(new { error = $"Session {sessionId} not found." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error retrieving chunk session status. SessionId: {SessionId}, Duration: {Duration}ms", 
                    sessionId, stopwatch.ElapsedMilliseconds);
                
                return StatusCode(
                    StatusCodes.Status500InternalServerError, 
                    new { error = "Failed to retrieve chunk session status." }
                );
            }
            finally 
            {
                stopwatch.Stop();
            }
        }

        // --- Private Helper Methods ---

        private string GetBucketForType(string? fileType) =>
            fileType?.ToLowerInvariant() switch
            {
                "image" => "images",
                "document" => "documents",
                "video" => "videos",
                _ => "other-uploads"
            };

        private Dictionary<string, string> GetSecurityTags(string fileType) =>
            new Dictionary<string, string>
            {
                ["FileType"] = fileType,
                ["UploadedBy"] = User.GetUserId() ?? "anonymous_or_system"
            };

        private string GenerateObjectName(string fileName, string userId)
        {
            var sanitizedFileName = Path.GetFileName(fileName);
            return $"{userId}/{Guid.NewGuid()}{Path.GetExtension(sanitizedFileName)}";
        }

        private ContentDto MapToDto(Content content) =>
            new ContentDto(
                content.Id,
                content.EntityId,
                content.EntityType,
                content.Path,
                content.ContentType.ToString(),
                content.FileSize);

        private ContentType ParseContentType(string mime) =>
            mime.ToLowerInvariant() switch
            {
                var m when m.StartsWith("image/") => Db.ContentType.Image,
                "application/pdf" => Db.ContentType.Document,
                var m when m.StartsWith("video/") => Db.ContentType.Video,
                _ => Db.ContentType.Other
            };
    }
}