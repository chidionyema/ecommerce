// Suggested location: ecommerce.Models/FileValidationResult.cs
namespace ecommerce.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Represents the result of a file validation operation.
    /// </summary>
    public record FileValidationResult
    {
        /// <summary>
        /// Gets the determined or claimed file type.
        /// For successful validations, this is guaranteed to be non-null.
        /// For failures, this can be null if the type could not be determined or is irrelevant.
        /// The meaning (MIME type vs. category like "image") depends on the validation method in FileValidator.
        /// </summary>
        public string? FileType { get; }

        /// <summary>
        /// Gets the collection of validation errors. Empty if validation is successful.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>
        /// Gets a value indicating whether the validation was successful (i.e., no errors).
        /// </summary>
        public bool IsValid => !Errors.Any();

        // Private constructor to ensure controlled creation via factory methods.
        private FileValidationResult(string? fileType, IEnumerable<string>? errors)
        {
            FileType = fileType;
            Errors = errors?.ToList().AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>
        /// Creates a success validation result.
        /// </summary>
        /// <param name="fileType">The validated file type. Must not be null or whitespace.</param>
        /// <returns>A new successful FileValidationResult.</returns>
        public static FileValidationResult Success(string fileType)
        {
            if (string.IsNullOrWhiteSpace(fileType))
            {
                // This ensures that if IsValid == true, FileType is always non-null.
                throw new ArgumentException("FileType cannot be null or whitespace for a successful validation.", nameof(fileType));
            }
            return new FileValidationResult(fileType, null); // No errors
        }

        /// <summary>
        /// Creates a failure validation result with a collection of errors.
        /// </summary>
        /// <param name="errors">The collection of validation errors. Must not be null or empty.</param>
        /// <param name="fileType">Optional: The file type, if known, despite the failure.</param>
        /// <returns>A new failed FileValidationResult.</returns>
        public static FileValidationResult Failure(IEnumerable<string> errors, string? fileType = null)
        {
            if (errors == null || !errors.Any())
            {
                throw new ArgumentException("Errors collection cannot be null or empty for a failure result.", nameof(errors));
            }
            return new FileValidationResult(fileType, errors);
        }

        /// <summary>
        /// Creates a failure validation result with a single error.
        /// </summary>
        /// <param name="error">The validation error. Must not be null or whitespace.</param>
        /// <param name="fileType">Optional: The file type, if known, despite the failure.</param>
        /// <returns>A new failed FileValidationResult.</returns>
        public static FileValidationResult Failure(string error, string? fileType = null)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("Error message cannot be null or whitespace.", nameof(error));
            }
            return new FileValidationResult(fileType, new[] { error });
        }
    }
}