#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ecommerce.Contracts;
using ecommerce.Dto;
using ecommerce.Models;
using Xunit;
using ecommerce.Infrastructure.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ecommerce.Db;

using ecommerce.Controllers;
using System.Text.Json;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace ecommerce.Tests.IntegrationTests.Controllers
{
    [Collection("Integration Tests")]
    public class ContentControllerTests : IAsyncLifetime
    {
        private readonly HttpClient _client;
        private readonly IntegrationTestFixture _fixture;

        public ContentControllerTests(IntegrationTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Factory.WithTestAuth().CreateClient();
        }

        public async Task InitializeAsync() => await _fixture.ResetDatabaseAsync();
        public Task DisposeAsync() => Task.CompletedTask;

                
        private MultipartFormDataContent CreateFileUploadContent(string fieldName, string fileName, string contentType, byte[] data)
        {
            var content = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(data);
            byteContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            content.Add(byteContent, fieldName, fileName);
            return content;
        }

        private async Task<string> DebugResponse(HttpResponseMessage response)
        {
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Status: {response.StatusCode}\nContent: {content}");
            return content;
        }

       [Fact]
    public async Task UploadFile_ValidFile_ReturnsCreatedContent()
    {
        // Create minimal valid JPEG file bytes (without System.Drawing)
         var jpegBytes = Convert.FromBase64String(
        "/9j/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/yQALCAABAAEBAREA/8wABgAQEAX/2gAIAQEAAD8A0I8g/9k="
        );

        var fileContent = CreateFileUploadContent(
            "file", 
            "test.jpg", 
            "image/jpeg", 
            jpegBytes);

        var response = await _client.PostAsync(
            "/api/v1/content/upload?entityId=" + Guid.NewGuid(), 
            fileContent);
        
        response.Should().HaveStatusCode(HttpStatusCode.Created);
    }


        [Fact]
        public async Task UploadFile_InvalidFileType_RejectsUpload()
        {
            var fileContent = CreateFileUploadContent(
                "file",
                "test.exe",
                "application/x-msdownload",
                Encoding.UTF8.GetBytes("Malicious content"));

            var response = await _client.PostAsync("/api/v1/content/upload?entityId=" + Guid.NewGuid(), fileContent);
            response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task GetContent_ExistingContent_ReturnsContentDto()
        {
            Content contentToCreate;
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
                contentToCreate = new Content
                {
                    Id = Guid.NewGuid(),
                    EntityId = Guid.NewGuid(),
                    EntityType = "documents",
                    ContentType = ContentType.Document, // Enum from ecommerce.Models or ecommerce.Db
                    FileSize = 1024,
                    Path = "/test/path",
                    ObjectName = "test-object",
                    BucketName = "documents",
                    CreatedAt = DateTime.UtcNow
                };
                await dbContext.Contents.AddAsync(contentToCreate);
                await dbContext.SaveChangesAsync();
            }

            var response = await _client.GetAsync($"/api/v1/content/{contentToCreate.Id}");
            response.Should().HaveStatusCode(HttpStatusCode.OK);
        }

        [Fact]
        public async Task GetContent_NonExistingContent_ReturnsNotFound()
        {
            var response = await _client.GetAsync($"/api/v1/content/{Guid.NewGuid()}");
            response.Should().HaveStatusCode(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task DeleteContent_RemovesFromDatabase()
        {
            Guid contentId;
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
                // Ensure all required fields for Content are populated if necessary for your DB context
                var content = new Content { 
                    Id = Guid.NewGuid(), 
                    BucketName = "test", 
                    ObjectName = "test",
                    EntityId = Guid.NewGuid(), // Added example required field
                    FileName = "testfile.txt", // Added example required field
                    ContentType = ContentType.Other, // Added example required field
                    FileSize = 0 // Added example required field
                };
                await context.Contents.AddAsync(content);
                await context.SaveChangesAsync();
                contentId = content.Id;
            }

            var response = await _client.DeleteAsync($"/api/v1/content/{contentId}");
            response.Should().HaveStatusCode(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task InitChunkSession_ValidRequest_ReturnsCreatedSession()
        {
            var request = new ChunkSessionRequest(
                EntityId: Guid.NewGuid(),
                ChunkSize: 1024 * 1024 * 10, // Example: 10MB chunk size
                FileName: "largefile.mp4",
                ContentType: "video/mp4",    // Added ContentType
                TotalChunks: 10,
                TotalSize: 1024 * 1024 * 100 // 100MB total size
            );

            var response = await _client.PostAsJsonAsync("/api/v1/content/chunked/init", request);
            response.Should().HaveStatusCode(HttpStatusCode.Created);
        }

        [Fact]
        public async Task InitChunkSession_InvalidSize_ReturnsBadRequest()
        {
            var request = new ChunkSessionRequest(
                EntityId: Guid.NewGuid(),
                ChunkSize: 1024 * 1024,   // Example: 1MB chunk size (valid for this parameter)
                FileName: "test.mp4",
                ContentType: "video/mp4", // Added ContentType
                TotalChunks: 0,           // Intentionally invalid for the test
                TotalSize: 0              // Intentionally invalid for the test
            );

            var response = await _client.PostAsJsonAsync("/api/v1/content/chunked/init", request);
            response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task UploadChunk_ValidChunk_StoresInTempStorage()
        {
            var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init", 
                new ChunkSessionRequest(
                    EntityId: Guid.NewGuid(),
                    ChunkSize: 1024,            // Calculated: TotalSize / TotalChunks if applicable, or a fixed chunk size
                    FileName: "test.mp4",
                    ContentType: "video/mp4",   // Added ContentType
                    TotalChunks: 3,
                    TotalSize: 3072
                ));
            
            var session = await initResponse.Content.ReadFromJsonAsync<ChunkSession>();
            var chunkContent = CreateFileUploadContent(
                "chunkFile",
                "chunk.bin",
                "application/octet-stream",
                new byte[1024]); // Ensure this matches the ChunkSize if that's the expectation

            var response = await _client.PostAsync(
                $"/api/v1/content/chunked/{session!.Id}/0", 
                chunkContent);
            
            response.Should().HaveStatusCode(HttpStatusCode.OK);
        }

        [Fact]
        public async Task UploadChunk_InvalidIndex_ReturnsBadRequest()
        {
            var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
                new ChunkSessionRequest(
                    EntityId: Guid.NewGuid(),
                    ChunkSize: 1024,
                    FileName: "test.mp4",
                    ContentType: "video/mp4",   // Added ContentType
                    TotalChunks: 3,
                    TotalSize: 3072
                ));
            
            var session = await initResponse.Content.ReadFromJsonAsync<ChunkSession>();

            var response = await _client.PostAsync(
                $"/api/v1/content/chunked/{session!.Id}/99", // Invalid index
                CreateFileUploadContent(
                    "chunkFile",
                    "chunk.bin",
                    "application/octet-stream",
                    new byte[1024]));

            response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
        }

        
    [Fact]
    public async Task CompleteChunkSession_ValidSession_AssemblesFile()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var fileName = "test.mp4";
        var totalChunks = 3;
        var chunkSize = 1024; // This is the size of each chunk
        var totalSize = totalChunks * chunkSize;
        
        Console.WriteLine($"Starting test with: EntityId={entityId}, TotalChunks={totalChunks}, ChunkSize={chunkSize}");

        // Initialize session
        var initResponse = await _client.PostAsJsonAsync(
            "/api/v1/content/chunked/init",
            new ChunkSessionRequest(
                EntityId: entityId,
                ChunkSize: chunkSize,       // Use the defined chunkSize
                FileName: fileName,
                ContentType: "video/mp4",   // Or "application/octet-stream" if filename implies different type
                TotalChunks: totalChunks,
                TotalSize: totalSize
            ));
        
        initResponse.EnsureSuccessStatusCode();
        var session = await initResponse.Content.ReadFromJsonAsync<ChunkSession>();
        Console.WriteLine($"Session created: {session!.Id}");
        
        var rng = new Random();

        // Upload chunks
        for (int i = 0; i < totalChunks; i++)
        {
            var chunkData = new byte[chunkSize]; // Each chunk has 'chunkSize'
            rng.NextBytes(chunkData);
            
            var chunkContent = CreateFileUploadContent(
                "chunkFile", 
                $"chunk{i}.bin", 
                "application/octet-stream", 
                chunkData);
                
            var chunkResponse = await _client.PostAsync(
                $"/api/v1/content/chunked/{session!.Id}/{i}", 
                chunkContent);
                
            chunkResponse.EnsureSuccessStatusCode();
            Console.WriteLine($"Uploaded chunk {i} - Status: {chunkResponse.StatusCode}");
            
            // Add a delay to ensure proper processing
            await Task.Delay(500);
        }

        // Complete session
        await Task.Delay(1000); // Ensure chunks are processed if async operations are involved on the server
        var completeResponse = await _client.PostAsync($"/api/v1/content/chunked/complete/{session!.Id}", null);
        
        // Debugging the response if it's not successful
        if (!completeResponse.IsSuccessStatusCode)
        {
            var errorContent = await DebugResponse(completeResponse);
            Console.WriteLine($"Error completing session: {errorContent}");
        }
        completeResponse.EnsureSuccessStatusCode(); 
        
        Console.WriteLine("Session completed successfully");
    }

    private async Task VerifyChunkStorage(
        IContentStorageService storageService,
        Guid sessionId,
        int chunkIndex,
        byte[] expectedData,
        string expectedChecksum)
    {
        const int maxRetries = 5;
        const int baseDelayMs = 2000; // Increase to 2 seconds
        var random = new Random();
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                
                // Download the chunk
                await using var stream = await storageService.DownloadAsync("temp-chunks", $"{sessionId}/{chunkIndex}");
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var actualData = ms.ToArray();
                
                // Log verification details
                Console.WriteLine($"Attempt {attempt}: Expected size: {expectedData.Length}, Actual size: {actualData.Length}");
                
                if (actualData.Length != expectedData.Length)
                {
                    // Detailed diagnostic on failure
                    Console.WriteLine($"Size mismatch! First 20 bytes expected: {BitConverter.ToString(expectedData.Take(20).ToArray())}");
                    Console.WriteLine($"Size mismatch! First 20 bytes actual: {BitConverter.ToString(actualData.Take(Math.Min(20, actualData.Length)).ToArray())}");
                    throw new InvalidOperationException($"Size mismatch on attempt {attempt}: Expected {expectedData.Length}, got {actualData.Length}");
                }
                
                // Content validation
                var actualChecksum = Convert.ToHexString(SHA256.HashData(actualData));
                if (actualChecksum != expectedChecksum)
                {
                    throw new InvalidOperationException($"Checksum mismatch on attempt {attempt}");
                }
                
                Console.WriteLine($"Chunk {chunkIndex} verified successfully on attempt {attempt}");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                // Exponential backoff with jitter
                int delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                int jitter = random.Next(-delayMs / 4, delayMs / 4);
                int actualDelay = delayMs + jitter;
                
                Console.WriteLine($"Attempt {attempt} failed: {ex.Message}. Retrying in {actualDelay}ms");
                await Task.Delay(actualDelay);
            }
        }
        
        throw new Exception($"Failed to verify chunk {chunkIndex} after {maxRetries} attempts");
    }

        [Fact]
        public async Task GetChunkSessionStatus_ValidSession_ReturnsProgress()
        {
            var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
                new ChunkSessionRequest(
                    EntityId: Guid.NewGuid(),
                    ChunkSize: 1048576,         // Example: 1MB (5242880 / 5)
                    FileName: "large.mp4",
                    ContentType: "video/mp4",   // Added ContentType
                    TotalChunks: 5,
                    TotalSize: 5242880
                ));
            
            var session = await initResponse.Content.ReadFromJsonAsync<ChunkSession>();

            var response = await _client.GetAsync($"/api/v1/content/chunked/session/{session!.Id}");
            response.Should().HaveStatusCode(HttpStatusCode.OK);
        }

        [Fact]
        public async Task CompleteChunkSession_IncompleteSession_ReturnsBadRequest()
        {
            var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
                new ChunkSessionRequest(
                    EntityId: Guid.NewGuid(),
                    ChunkSize: 1024,            // Example: (3072 / 3)
                    FileName: "largefile.mp4",
                    ContentType: "video/mp4",   // Added ContentType
                    TotalChunks: 3,
                    TotalSize: 3072
                ));
            
            var session = await initResponse.Content.ReadFromJsonAsync<ChunkSession>();

            // Not uploading any chunks, then trying to complete
            var response = await _client.PostAsync(
                $"/api/v1/content/chunked/complete/{session!.Id}", 
                null);

            response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task UploadChunk_InvalidSession_ReturnsNotFound()
        {
            var chunkContent = CreateFileUploadContent(
                "chunkFile",
                "chunk.bin",
                "application/octet-stream",
                new byte[1024]);

            var response = await _client.PostAsync(
                $"/api/v1/content/chunked/{Guid.NewGuid()}/0", // Non-existent session
                chunkContent);

            response.Should().HaveStatusCode(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task GetChunkSessionStatus_NonExistingSession_ReturnsNotFound()
        {
            var response = await _client.GetAsync($"/api/v1/content/chunked/session/{Guid.NewGuid()}");
            response.Should().HaveStatusCode(HttpStatusCode.NotFound);
        }
    }
}