using System;
namespace ecommerce.Dto
{
    public record ProductReviewDto
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public int Rating { get; init; }
        public bool IsVerified { get; init; }
        public Guid ProductId { get; init; }
        public Guid? UserId { get; init; }
        public string? UserName { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }
    
    public record ProductReviewCreateDto
    {
        public string Title { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public int Rating { get; init; }
        public Guid ProductId { get; init; }
    }
}
