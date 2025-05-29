using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ecommerce.Db
{
    public enum HandlerType
    {
        Payment,
        Subscription,
        Unknown
    }

    public class WebhookEvent : AuditableEntity
    {
        // Unique Stripe event identifier.
        public string StripeEventId { get; set; } = string.Empty;
        
        public string EventType { get; set; } = string.Empty;
        public string EventJson { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
        public bool IsProcessed { get; set; }
        
        // Using an enum for HandlerType ensures only allowed values are used.
        public HandlerType HandlerType { get; set; } = HandlerType.Unknown;
        
        public string Error { get; set; } = string.Empty;
        
        // The RowVersion property is inherited from AuditableEntity.
        // If needed, you can override it here with additional annotations.
    }
}
