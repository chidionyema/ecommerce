using System;

namespace ecommerce.Db
{
    public class Subscription : AuditableEntity
    {
        public string UserId { get; set; } = string.Empty;
        public string StripeSubscriptionId { get; set; } = string.Empty;
        public string PlanId { get; set; } = string.Empty;
        public SubscriptionStatus Status { get; set; }
        public User User { get; set; }
        public DateTime StartsAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public class SubscriptionPlan : AuditableEntity
    {
        public string Name { get; set; } = string.Empty;
        public string StripePriceId { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string? Description { get; set; }
    }

    public enum SubscriptionStatus
    {
        Active,
        Canceled,
        Incomplete,
        Unknown,
        PastDue,
        Trialing,
        Expired,
        Unpaid
    }
}
