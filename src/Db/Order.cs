using System;
using System.Collections.Generic;

namespace ecommerce.Db
{
    public class Order : AuditableEntity
    {
        public Order() : base() { }

        public Order(Guid id) : base(id) { }
        public Order(Guid id, decimal totalAmount, OrderStatus status, string? userId = null) 
            : base(id)
        {
            TotalAmount = totalAmount;
            Status = status;
            UserId = userId ?? string.Empty;
            OrderItems = new List<OrderItem>(); 
        }

        public string UserId { get; set; } = string.Empty;
        // For guest checkout
        public string? GuestEmail { get; set; }  
        public string? GuestFirstName { get; set; } 
        public string? GuestLastName { get; set; }
        public virtual GuestOrderInfo? GuestInfo { get; set; }

        public string IdempotencyKey { get; set; } = string.Empty;
        public ICollection<OrderItem>? OrderItems { get; set; }
        public decimal TotalAmount { get; set; }
        public OrderStatus Status { get; set; } 
        public bool IsPaymentComplete { get; set; } 
        public Payment? Payment { get; set; }
    }
}
