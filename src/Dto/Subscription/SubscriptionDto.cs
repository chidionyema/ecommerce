using System;
using System.ComponentModel.DataAnnotations;

namespace ecommerce.Dto
{
    public record SubscriptionRequest
    {
        [Required]
        public string PriceId { get; init; } = string.Empty;
        public decimal Amount { get; set; }
        
        [Required]
        public string RedirectPath { get; init; } = string.Empty;
    }

    public record SubscriptionStatusResponseDto
    {
        public bool IsSubscribed { get; init; }
        public string? PlanId { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    public record CreateCheckoutSessionResponseDto
    {
        public string SessionId { get; init; } = string.Empty;
    }
}
