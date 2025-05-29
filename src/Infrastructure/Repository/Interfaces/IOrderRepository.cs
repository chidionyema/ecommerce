using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ecommerce.Db; 
using Microsoft.EntityFrameworkCore.Storage;
using System.Data; // For IsolationLevel

namespace ecommerce.Infrastructure.Repository.Interfaces
{
    public interface IOrderRepository
    {
        // Existing Order methods
        Task<IEnumerable<Order>> GetOrdersAsync(); // General purpose, might be filtered by user in service
        Task<Order?> GetOrderByIdAsync(Guid id); 
        Task CreateOrderAsync(Order order);
        Task UpdateOrderStatusAsync(Guid orderId, OrderStatus status);
        Task<Order?> GetOrderByIdempotencyKeyAsync(string idempotencyKey);
        
        // Transaction and SaveChanges
        Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted);
        Task SaveChangesAsync();

        // Subscription and Plan methods
        Task<SubscriptionPlan?> GetSubscriptionPlanByPriceIdAsync(string stripePriceId);
        Task<Subscription?> GetSubscriptionByStripeIdAsync(string stripeSubscriptionId);
        Task<Subscription?> GetSubscriptionByUserIdAsync(string userId);
        Task CreateSubscriptionAsync(Subscription subscription);
        Task UpdateSubscriptionAsync(Subscription subscription);
        Task<bool> ValidateSubscriptionPriceAsync(string priceId, decimal expectedAmount);

        // Payment methods
        Task<Payment?> GetPaymentByStripeSessionIdAsync(string stripeSessionId);
        Task<Payment?> GetPaymentByOrderIdAsync(Guid orderId); // Added
        Task CreatePaymentAsync(Payment payment); 
        Task UpdatePaymentAsync(Payment payment); 

        // Guest Order Info methods
        Task SaveGuestOrderInfoAsync(GuestOrderInfo guestInfo); // Added
        Task SaveGuestOrderTokenAsync(Guid orderId, string orderToken); // Added
        Task<GuestOrderInfo?> GetGuestOrderInfoAsync(Guid orderId); // Added
        
        // Method to get all orders (potentially for admin or internal use, controller filters further)
        Task<IEnumerable<Order>> GetAllOrdersAsync(); // Added
    }
}
