using System;

namespace ecommerce.Db
{
    public class Payment : AuditableEntity
    {
        public Payment() : base() { }

        public Payment(Guid id) : base(id) { }

        public Guid OrderId { get; set; }
        public Order? Order { get; set; }
        
        public string UserId { get; set; } = string.Empty;

        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
        public string PaymentMethod { get; set; } = string.Empty;
        
        // Additional payment properties
        public decimal Tax { get; set; } = 0;
        public bool IsComplete { get; set; } = false;
        public DateTime OrderDate { get; set; } = DateTime.UtcNow;
        
        // Stripe specific fields
        public string? StripeSessionId { get; set; }
        public string? StripePaymentIntentId { get; set; }
        public string? TransactionId { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUpdatedAtUtc { get; set; }
    }

    public enum PaymentStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Refunded,
        Cancelled
    }
}