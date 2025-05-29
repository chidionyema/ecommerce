using Microsoft.AspNetCore.Identity;
using System.Collections.Generic;
using System;
namespace ecommerce.Db
{
     public class User : IdentityUser
    {
        public string? CheckoutSessionId { get; set; }
        public string? StripeCustomerId { get; set; }
        public virtual UserProfile? Profile { get; set; }

        // Add any additional user properties here
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public bool IsActive { get; set; } = true;
    }
}