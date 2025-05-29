using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using ecommerce.Contracts;
using ecommerce.Dto;
using ecommerce.PolicyFactory;
using Polly;
using System.Text; // For Encoding
using Minio.Exceptions; // For MinioException
using System.Security.Cryptography; // For SHA256


namespace ecommerce.Services
{
    public class ContentStorageService : IContentStorageService
    {
        private readonly IMinioClient _minioClient;
        private readonly ILogger<ContentStorageService> _logger;
        private readonly IAsyncPolicy _resiliencePolicy;

        public ContentStorageService(
            IMinioClient minioClient,
            ILogger<ContentStorageService> logger,
            IConfiguration config)
        {
            _minioClient = minioClient ?? throw new ArgumentNullException(nameof(minioClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resiliencePolicy = ResiliencePolicyFactory.CreateVaultPolicy(config, logger);
        }

        public async Task<ContentUploadResult> UploadAsync(
            Stream fileStream,
            string bucketName,
            string objectName,
            string contentType,
            IDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) // Added CancellationToken
        {
            ContentUploadResult? result = null;
            await _resiliencePolicy.ExecuteAsync(
                async (context, ct) => // Correct lambda parameters
                {
                    var putObjectArgs = new PutObjectArgs()
                        .WithBucket(bucketName)
                        .WithObject(objectName)
                        .WithStreamData(fileStream)
                        .WithObjectSize(fileStream.Length)
                        .WithContentType(contentType)
                        .WithHeaders(metadata);

                    await _minioClient.PutObjectAsync(putObjectArgs, ct); // Use ct here

                    result = new ContentUploadResult(
                        bucketName,
                        objectName,
                        contentType,
                        fileStream.Length,
                        VersionId: string.Empty,
                        StorageDetails: string.Empty,
                        Path: string.Empty
                    );
                },
                new Context(), // Pass Polly context
                cancellationToken
            );
            
            return result ?? throw new InvalidOperationException("Upload failed");
        }

        public async Task<string> GetPresignedUrlAsync(
            string bucketName,
            string objectName,
            TimeSpan expiry,
            bool requireAuth = true,
            CancellationToken cancellationToken = default) // Added CancellationToken
        {
            string? url = null;
            await _resiliencePolicy.ExecuteAsync(
                async (context, ct) => // Correct lambda parameters
                {
                    var args = new PresignedGetObjectArgs()
                        .WithBucket(bucketName)
                        .WithObject(objectName)
                        .WithExpiry((int)expiry.TotalSeconds);

                    url = await _minioClient.PresignedGetObjectAsync(args);
                },
                new Context(),
                cancellationToken
            );
            
            return url ?? throw new InvalidOperationException("Failed to generate URL");
        }

  public async Task<Stream> DownloadAsync(string bucketName, string objectKey, CancellationToken cancellationToken = default)
{
    try {
        // Create a memory stream to hold the object data
        var memoryStream = new MemoryStream();
        
        // Get the object and copy its contents to our memory stream
        await _minioClient.GetObjectAsync(
            new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectKey)
                .WithCallbackStream(stream => {
                    // Copy the stream content to memory stream
                    stream.CopyTo(memoryStream);
                }),
            cancellationToken);
        
        // Reset memory stream position for reading
        memoryStream.Position = 0;
        
        // Check if we got XML (which likely indicates an error) instead of actual data
        if (memoryStream.Length > 5)
        {
            byte[] buffer = new byte[5];
            memoryStream.Read(buffer, 0, 5);
            memoryStream.Position = 0; // Reset position
            
            string prefix = Encoding.ASCII.GetString(buffer);
            if (prefix == "<?xml")
            {
                using var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string xml = reader.ReadToEnd();
                memoryStream.Position = 0;
                
                _logger.LogError("Received XML response instead of object data: {Xml}", xml);
                throw new InvalidOperationException($"Failed to download object {objectKey} from {bucketName}: Server returned XML");
            }
        }
        
        return memoryStream;
    }
    catch (Exception ex) when (ex is MinioException || ex is ObjectNotFoundException || ex is BucketNotFoundException)
    {
        _logger.LogError(ex, "Error downloading object {ObjectKey} from bucket {BucketName}", objectKey, bucketName);
        throw;
    }
}

        public async Task DeleteAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken = default) // Added CancellationToken
        {
            await _resiliencePolicy.ExecuteAsync(
                async (context, ct) => // Correct lambda parameters
                {
                    await _minioClient.RemoveObjectAsync(
                        new RemoveObjectArgs()
                            .WithBucket(bucketName)
                            .WithObject(objectName),
                        ct // Use ct here
                    );
                },
                new Context(),
                cancellationToken
            );
        }

        public async Task EnsureBucketExistsAsync(
            string bucketName,
            CancellationToken cancellationToken = default) // Added CancellationToken
        {
            await _resiliencePolicy.ExecuteAsync(
                async (context, ct) => // Correct lambda parameters
                {
                    bool exists = await _minioClient.BucketExistsAsync(
                        new BucketExistsArgs().WithBucket(bucketName),
                        ct // Use ct here
                    );
                    
                    if (!exists)
                    {
                        await _minioClient.MakeBucketAsync(
                            new MakeBucketArgs().WithBucket(bucketName),
                            ct // Use ct here
                        );
                        await SetBucketPolicyAsync(bucketName, ct);
                    }
                },
                new Context(),
                cancellationToken
            );
        }

        private async Task SetBucketPolicyAsync(
            string bucketName,
            CancellationToken ct) // Already has CancellationToken
        {
            var policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    {{
                        ""Effect"": ""Deny"",
                        ""Principal"": ""*"",
                        ""Action"": ""s3:*"",
                        ""Resource"": ""arn:aws:s3:::{bucketName}/*"",
                        ""Condition"": {{
                            ""Bool"": {{ ""aws:SecureTransport"": ""false"" }}
                        }}
                    }}
                ]
            }}";

            await _minioClient.SetPolicyAsync(
                new SetPolicyArgs()
                    .WithBucket(bucketName)
                    .WithPolicy(policy),
                ct // Use ct here
            );
        }
    }
}