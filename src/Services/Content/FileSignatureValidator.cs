// File: ecommerce.Services/FileSignatureValidator.cs
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats; // Required for IImageFormat
using System;
using System.IO;
using System.Threading.Tasks;
using ecommerce.Contracts;
using ecommerce.Models;
using Microsoft.Extensions.Logging; // For logging

namespace ecommerce.Services
{
    public class FileSignatureValidator : IFileSignatureValidator
    {
        private readonly ILogger<FileSignatureValidator> _logger;

        public FileSignatureValidator(ILogger<FileSignatureValidator> logger) // Inject logger
        {
            _logger = logger;
        }

        public async Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream)
        {
            try
            {
                fileStream.Position = 0;
                // Using a CancellationToken can be beneficial for async operations
                var imageInfo = await Image.IdentifyAsync(fileStream); // Consider Image.IdentifyAsync(config, stream, CancellationToken)

                if (imageInfo != null)
                {
                    // Get the default MIME type for the detected image format
                    // IImageInfo contains Metadata.DecodedImageFormat which is IImageFormat
                    string specificMimeType = imageInfo.Metadata?.DecodedImageFormat?.DefaultMimeType;

                    if (!string.IsNullOrWhiteSpace(specificMimeType))
                    {
                        _logger.LogInformation("File identified as image. Detected MIME type: {MimeType}", specificMimeType);
                        return new FileSignatureValidationResult(true, specificMimeType);
                    }
                    else
                    {
                        // This case means ImageSharp identified it, but we couldn't get a specific MIME type.
                        // This might happen for very obscure or new formats ImageSharp partly supports.
                        _logger.LogWarning("File identified as image, but specific MIME type could not be determined. Format: {FormatName}", imageInfo.Metadata?.DecodedImageFormat?.Name);
                        // Fallback to generic "image" - this will likely still cause IsContentTypeConsistent to fail unless it's adapted
                        return new FileSignatureValidationResult(true, "image");
                    }
                }

                // Add other file type validations here if needed (e.g., for PDFs, videos using other libraries)
                // For PDF: Check for %PDF- header
                // For Video: This is harder with just stream identification without parsing significant portions.

                _logger.LogWarning("File signature validation failed: File is not a recognized image format or other configured type.");
                return new FileSignatureValidationResult(false, "unknown");
            }
            catch (UnknownImageFormatException ex) // More specific exception
            {
                _logger.LogWarning(ex, "File signature validation failed: Image format is unknown or unsupported.");
                return new FileSignatureValidationResult(false, "unsupported_image_format");
            }
            catch (InvalidImageContentException ex) // More specific exception for corrupted images
            {
                 _logger.LogWarning(ex, "File signature validation failed: Image content is invalid or corrupted.");
                return new FileSignatureValidationResult(false, "invalid_image_content");
            }
            catch (Exception ex) // General catch-all
            {
                _logger.LogError(ex, "An unexpected error occurred during file signature validation.");
                return new FileSignatureValidationResult(false, "validation_error"); // Use a distinct type for errors vs unknown
            }
            finally
            {
                // It's good practice to ensure the stream is reset if the caller might reuse it,
                // though in this specific chain, FileValidator does its own resets.
                if (fileStream.CanSeek)
                {
                    fileStream.Position = 0;
                }
            }
        }
    }
}