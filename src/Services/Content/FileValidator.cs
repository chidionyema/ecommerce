// File: ecommerce.Services/FileValidator.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ecommerce.Contracts;
using ecommerce.Models;

namespace ecommerce.Services
{
    public class FileValidator : IFileValidator
    {
        private readonly IFileSignatureValidator _signatureValidator;
        private readonly IVirusScanner _virusScanner;
        private readonly ILogger<FileValidator> _logger;

        // Configuration: These should ideally be injected (e.g., via IOptions<FileValidationSettings>)
        private static readonly List<string> AllowedContentTypes = new List<string> {
            "image/jpeg", "image/png", "image/gif", "image/webp",
            "application/pdf", "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "video/mp4", "video/quicktime"
        };
        private static readonly long MaxFileSize = 10L * 1024 * 1024 * 1024; // 10 GB
        private static readonly long MinFileSizeForStreamValidation = 1; // For ValidateAsync(IFormFile)
        private static readonly long MinFileSizeForMetadata = 0;       // For ValidateMetadataAsync

        public FileValidator(
            IFileSignatureValidator signatureValidator,
            IVirusScanner virusScanner,
            ILogger<FileValidator> logger)
        {
            _signatureValidator = signatureValidator ?? throw new ArgumentNullException(nameof(signatureValidator));
            _virusScanner = virusScanner ?? throw new ArgumentNullException(nameof(virusScanner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<FileValidationResult> ValidateAsync(IFormFile file)
        {
            try
            {
                _logger.LogInformation("Starting full file validation for: {FileName}, Declared ContentType: {ContentType}, Size: {FileSize}",
                    file.FileName, file.ContentType, file.Length);

                if (file.Length < MinFileSizeForStreamValidation)
                {
                    const string error = "File is empty or too small for stream validation.";
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "SizeCheck", file.FileName, error);
                    return FileValidationResult.Failure(error, file.ContentType); // Pass declared ContentType
                }

                if (file.Length > MaxFileSize)
                {
                    string error = $"File size ({file.Length} bytes) exceeds maximum allowed limit of {MaxFileSize / (1024 * 1024)} MB.";
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "SizeCheck", file.FileName, error);
                    return FileValidationResult.Failure(error, file.ContentType);
                }

                if (!AllowedContentTypes.Contains(file.ContentType.ToLowerInvariant()))
                {
                    string error = $"Declared ContentType '{file.ContentType}' is not allowed.";
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "ContentTypeCheck", file.FileName, error);
                    // Continue to signature validation to determine actual type, but this is an early warning/failure point.
                    // Depending on policy, you might return FileValidationResult.Failure(error, file.ContentType) here.
                }

                await using var stream = file.OpenReadStream();

                _logger.LogDebug("Performing signature validation for {FileName}", file.FileName);
                FileSignatureValidationResult signatureResult = await _signatureValidator.ValidateAsync(stream); // Assuming this returns { bool IsValid, string FileType }
                if (!signatureResult.IsValid || string.IsNullOrWhiteSpace(signatureResult.FileType))
                {
                    string error = "Invalid file signature or type could not be determined. Detected: " + (signatureResult.FileType ?? "unknown");
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "SignatureValidation", file.FileName, error);
                    return FileValidationResult.Failure(error, signatureResult.FileType ?? file.ContentType);
                }
                _logger.LogInformation("Signature validation successful for {FileName}. Detected type: {FileType}", file.FileName, signatureResult.FileType);

                if (!IsContentTypeConsistent(file.ContentType, signatureResult.FileType))
                {
                     string error = $"Declared ContentType '{file.ContentType}' does not match actual file type '{signatureResult.FileType}'.";
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "ContentTypeConsistency", file.FileName, error);
                    return FileValidationResult.Failure(error, signatureResult.FileType);
                }
                 // Also check if the *detected* signature FileType is in the allowed list
                if (!AllowedContentTypes.Contains(signatureResult.FileType.ToLowerInvariant()))
                {
                    string error = $"Detected file type '{signatureResult.FileType}' is not allowed.";
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "AllowedTypeCheck", file.FileName, error);
                    return FileValidationResult.Failure(error, signatureResult.FileType);
                }


                stream.Position = 0;
                _logger.LogDebug("Performing virus scan for {FileName}", file.FileName);
                VirusScanResult scanResult = await _virusScanner.ScanAsync(stream); // Assuming this returns { bool IsMalicious, string ThreatName }
                if (scanResult.IsMalicious)
                {
                    string error = "File contains malicious content. Threat: " + (scanResult.ThreatName ?? "unknown");
                    _logger.LogWarning("{ValidationStep} failed for {FileName}: {Error}", "VirusScan", file.FileName, error);
                    return FileValidationResult.Failure(error, signatureResult.FileType);
                }
                _logger.LogInformation("Virus scan successful for {FileName}. No threats detected.", file.FileName);

                _logger.LogInformation("Full file validation succeeded for: {FileName}, Type: {FileType}", file.FileName, signatureResult.FileType);
                return FileValidationResult.Success(signatureResult.FileType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during full file validation for {FileName}", file?.FileName ?? "N/A");
                return FileValidationResult.Failure("An unexpected error occurred during file validation.", file?.ContentType);
            }
        }

        public Task<FileValidationResult> ValidateMetadataAsync(string fileName, string contentType, long totalSize)
        {
            _logger.LogInformation("Starting metadata validation. FileName: {FileName}, ContentType: {ContentType}, TotalSize: {TotalSize}",
                fileName, contentType, totalSize);

            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(fileName))
                errors.Add("FileName cannot be empty.");
            else
            {
                if (fileName.Length > 255) errors.Add("FileName exceeds maximum length of 255 characters.");
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) errors.Add("FileName contains invalid characters.");
            }

            if (string.IsNullOrWhiteSpace(contentType))
                errors.Add("ContentType cannot be empty.");
            else if (!AllowedContentTypes.Contains(contentType.ToLowerInvariant()))
                errors.Add($"ContentType '{contentType}' is not allowed by the server.");

            if (totalSize < MinFileSizeForMetadata)
                errors.Add($"TotalSize ({totalSize} bytes) cannot be less than {MinFileSizeForMetadata} bytes.");
            if (totalSize > MaxFileSize)
                errors.Add($"TotalSize ({totalSize} bytes) exceeds the maximum allowed limit of {MaxFileSize / (1024 * 1024)} MB.");

            if (errors.Any())
            {
                _logger.LogWarning("Metadata validation failed for FileName: {FileName}. Errors: {ValidationErrors}",
                    fileName, string.Join("; ", errors));
                return Task.FromResult(FileValidationResult.Failure(errors, contentType));
            }

            _logger.LogInformation("Metadata validation succeeded for FileName: {FileName}", fileName);
            return Task.FromResult(FileValidationResult.Success(contentType));
        }

        private bool IsContentTypeConsistent(string declaredContentType, string detectedSignatureFileType)
        {
            if (string.IsNullOrWhiteSpace(declaredContentType) || string.IsNullOrWhiteSpace(detectedSignatureFileType))
                return false;
            // Basic check, might need a more sophisticated mapping for aliases (e.g., image/jpg vs image/jpeg)
            return declaredContentType.ToLowerInvariant().Split(';')[0].Trim() == detectedSignatureFileType.ToLowerInvariant();
        }
    }
}