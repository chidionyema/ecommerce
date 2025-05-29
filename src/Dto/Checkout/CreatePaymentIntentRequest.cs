using System;
using System.ComponentModel.DataAnnotations; 

namespace ecommerce.Dto
{ 
    public class CreatePaymentIntentRequest
    {  
        [Required]
        public Guid OrderId { get; set; }
        
        [Required]
        public CheckoutItem[] Items { get; set; } = Array.Empty<CheckoutItem>();
        
        [Required]
        public decimal TotalAmount { get; set; } 
        
        [Required]
        public UserInfo UserInfo { get; set; } = new UserInfo();
    } 
    
    public class CreatePaymentIntentResponse
    {
        // Initialized with a default empty string to ensure it's never null.
        public string SessionId { get; set; } = string.Empty;
    }
    
    public class ConfirmPaymentRequest
    {
        // Initialized with a default empty string to ensure it's never null.
        public string SessionId { get; set; } = string.Empty; // The Stripe Checkout Session ID
        
        public Guid OrderId { get; set; }    // The Order ID generated during checkout
    }
}
