using System;
using Microsoft.AspNetCore.Identity;

namespace ecommerce.Db
{
    public class UserProfile : AuditableEntity
    {
        public string UserId { get; set; } = string.Empty;
        
        public string FirstName { get; set; } = string.Empty;
        
        public string LastName { get; set; } = string.Empty;
        
        public string Phone { get; set; } = string.Empty;
        
        public string Address { get; set; } = string.Empty;
        
        public string City { get; set; } = string.Empty;
        
        public string State { get; set; } = string.Empty;
        
        public string PostalCode { get; set; } = string.Empty;
        
        public string Country { get; set; } = "US";
        
        public string Bio { get; set; } = string.Empty;
        
        public string Website { get; set; } = string.Empty;
        
        public string AvatarUrl { get; set; } = string.Empty;
        
        public DateTime? UpdatedAt { get; set; }
        
        public DateTime? LastLogin { get; set; }
        
        public User? User { get; set; }
    }
}