using System;
using Microsoft.AspNetCore.Identity; // Assuming 'User' is from here or a similar custom type
using System.ComponentModel.DataAnnotations;
namespace ecommerce.Db
{
    public class RefreshToken : AuditableEntity
    {
        // Foreign key to the User
        public string UserId { get; set; } = string.Empty;
        
        // Navigation property to the Identity User (or your custom User type)
        public User User { get; set; } = null!;

        // The token string
        public string Token { get; set; } = string.Empty;
        
        // Expiration date for the token
        public DateTime Expires { get; set; }
    }


    public class RevokedToken : AuditableEntity
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(500)]
        public string Token { get; set; } = string.Empty;

        [Required]
        public DateTime RevokedAt { get; set; }

        [MaxLength(200)]
        public string? Reason { get; set; }

        [MaxLength(450)] // Standard ASP.NET Identity user ID length
        public string? UserId { get; set; }

        public DateTime ExpiresAt { get; set; }

        // Navigation property if you want to link to User
        public virtual User? User { get; set; }
    }
}