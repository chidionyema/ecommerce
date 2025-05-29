using Minio;
using Minio.DataModel.Args;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace ecommerce.Tests
{
    public static class MinioTestClient
    {
        private static IMinioClient _client;
        private static readonly object _lock = new object();

        public static IMinioClient Get(IConfiguration config)
        {
            if (_client == null)
            {
                lock (_lock)
                {
                    if (_client == null)
                    {
                        string endpoint = config["MinIO:Endpoint"] ?? "localhost:9001";
                        string accessKey = config["MinIO:AccessKey"] ?? "minioadmin"; 
                        string secretKey = config["MinIO:SecretKey"] ?? "minioadmin";
                        
                        _client = new MinioClient()
                            .WithEndpoint(endpoint)
                            .WithCredentials(accessKey, secretKey)
                            .WithSSL(false)
                            .Build();
                        
                        // Ensure test buckets exist
                        EnsureBucketsExistAsync(_client).GetAwaiter().GetResult();
                    }
                }
            }
            
            return _client;
        }

        private static async Task EnsureBucketsExistAsync(IMinioClient client)
        {
            var requiredBuckets = new[] { "temp-chunks", "final-content", "other" };
            
            foreach (var bucket in requiredBuckets)
            {
                try 
                {
                    bool exists = await client.BucketExistsAsync(
                        new BucketExistsArgs().WithBucket(bucket));
                        
                    if (!exists)
                    {
                        await client.MakeBucketAsync(
                            new MakeBucketArgs().WithBucket(bucket));
                            
                        // Set very permissive policy for testing
                        var policy = $@"{{
                            ""Version"": ""2012-10-17"",
                            ""Statement"": [
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Principal"": {{""AWS"": [""*""]}},
                                    ""Action"": [""s3:*""],
                                    ""Resource"": [""arn:aws:s3:::{bucket}/*"", ""arn:aws:s3:::{bucket}""]
                                }}
                            ]
                        }}";
                        
                        await client.SetPolicyAsync(
                            new SetPolicyArgs()
                                .WithBucket(bucket)
                                .WithPolicy(policy));
                    }
                }
                catch (Exception) { /* Ignore errors during bucket creation */ }
            }
        }
    }
}