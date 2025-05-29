using ecommerce.Db;
using System;
using System.Collections.Generic;
using System.Linq; // Required for Linq operations like OrderByDescending
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;
using ecommerce.Infrastructure.Repository.Interfaces;
 // Assuming ecommerceContext is here
using System.Data; // For IsolationLevel

namespace ecommerce.Infrastructure.Repository
{
    public class OrderRepository : IOrderRepository
    {
        private readonly ecommerceContext _context;
        private readonly ILogger<OrderRepository> _logger;

        public OrderRepository(ecommerceContext context, ILogger<OrderRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Order?> GetOrderByIdempotencyKeyAsync(string idempotencyKey)
        {
            if (string.IsNullOrEmpty(idempotencyKey))
            {
                _logger.LogWarning("GetOrderByIdempotencyKeyAsync called with null or empty idempotencyKey.");
                return null;
            }
            _logger.LogInformation("Fetching order by idempotency key: {IdempotencyKey}", idempotencyKey);
            return await _context.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems!) 
                    .ThenInclude(oi => oi.Product)
                .Include(o => o.Payment) 
                .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey);
        }

        public async Task<IEnumerable<Order>> GetOrdersAsync()
        {
            try
            {
                _logger.LogInformation("Fetching orders (potentially for a specific user, filter in service layer).");
                return await _context.Orders
                    .AsNoTracking()
                    .Include(o => o.OrderItems!) 
                        .ThenInclude(oi => oi.Product)
                    .Include(o => o.Payment)
                    .OrderByDescending(o => o.CreatedAt) 
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching orders.");
                throw; 
            }
        }
        
        // Implementation for GetAllOrdersAsync
        public async Task<IEnumerable<Order>> GetAllOrdersAsync()
        {
            try
            {
                _logger.LogInformation("Fetching ALL orders with no tracking (admin/internal use).");
                return await _context.Orders
                    .AsNoTracking()
                    .Include(o => o.OrderItems!) 
                        .ThenInclude(oi => oi.Product)
                    .Include(o => o.Payment)
                    .Include(o => o.GuestInfo) // Include guest info if relevant for an admin view
                    .OrderByDescending(o => o.CreatedAt) 
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching all orders.");
                throw; 
            }
        }

        public async Task<Order?> GetOrderByIdAsync(Guid id)
        {
            try
            {
                _logger.LogInformation("Fetching order with ID: {OrderId} using no tracking.", id);
                var order = await _context.Orders
                    .AsNoTracking()
                    .Include(o => o.OrderItems!)
                        .ThenInclude(oi => oi.Product)
                    .Include(o => o.Payment) 
                    .Include(o => o.GuestInfo) // Also include GuestInfo
                    .FirstOrDefaultAsync(o => o.Id == id);

                if (order == null)
                {
                    _logger.LogWarning("Order with ID {OrderId} not found.", id);
                }
                return order;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching order with ID {OrderId}.", id);
                throw;
            }
        }

        public async Task CreateOrderAsync(Order order)
        {
            try
            {
                if (order == null)
                {
                    throw new ArgumentNullException(nameof(order), "Order cannot be null.");
                }

                _logger.LogInformation("Creating a new order with potential ID: {OrderId}", order.Id);
                await _context.Orders.AddAsync(order);
                await _context.SaveChangesAsync(); 
                _logger.LogInformation("Order created successfully with ID: {OrderId}", order.Id);
            }
            catch (DbUpdateException ex) 
            {
                _logger.LogError(ex, "Database error occurred while creating a new order (ID if generated: {OrderId}).", order.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while creating a new order (ID if generated: {OrderId}).", order.Id);
                throw;
            }
        }

        public async Task UpdateOrderStatusAsync(Guid orderId, OrderStatus status)
        {
            try
            {
                _logger.LogInformation("Updating status for order ID: {OrderId} to {Status}.", orderId, status);
                var order = await _context.Orders.FindAsync(orderId); 

                if (order == null)
                {
                    _logger.LogWarning("Order with ID {OrderId} not found. Status update skipped.", orderId);
                    return; 
                }

                order.Status = status;
                await _context.SaveChangesAsync();
                _logger.LogInformation("Order status updated successfully for ID: {OrderId}.", orderId);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                 _logger.LogError(ex, "Concurrency conflict while updating status for order ID {OrderId}.", orderId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while updating the status for order ID {OrderId}.", orderId);
                throw;
            }
        }
        
        public async Task<SubscriptionPlan?> GetSubscriptionPlanByPriceIdAsync(string stripePriceId)
        {
            if (string.IsNullOrEmpty(stripePriceId))
            {
                _logger.LogWarning("GetSubscriptionPlanByPriceIdAsync called with null or empty stripePriceId.");
                return null;
            }
            _logger.LogInformation("Fetching subscription plan by Stripe Price ID: {StripePriceId}", stripePriceId);
            try
            {
                return await _context.SubscriptionPlans 
                    .AsNoTracking()
                    .FirstOrDefaultAsync(sp => sp.StripePriceId == stripePriceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching subscription plan by Stripe Price ID: {StripePriceId}", stripePriceId);
                throw;
            }
        }

        public async Task<Subscription?> GetSubscriptionByStripeIdAsync(string stripeSubscriptionId)
        {
            if (string.IsNullOrEmpty(stripeSubscriptionId))
            {
                 _logger.LogWarning("GetSubscriptionByStripeIdAsync called with null or empty stripeSubscriptionId.");
                return null;
            }
            _logger.LogInformation("Fetching subscription by Stripe Subscription ID: {StripeSubscriptionId}", stripeSubscriptionId);
            try
            {
                return await _context.Subscriptions 
                    .AsNoTracking()
                    .Include(s => s.User) 
                    .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching subscription by Stripe Subscription ID: {StripeSubscriptionId}", stripeSubscriptionId);
                throw;
            }
        }
        
        public async Task<Subscription?> GetSubscriptionByUserIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetSubscriptionByUserIdAsync called with null or empty userId.");
                return null;
            }
            _logger.LogInformation("Fetching active or trialing subscription by User ID: {UserId}", userId);
            try
            {
                return await _context.Subscriptions
                    .AsNoTracking()
                    .Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
                    .OrderByDescending(s => s.CreatedAt) 
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching subscription by User ID: {UserId}", userId);
                throw;
            }
        }

        public async Task CreateSubscriptionAsync(Subscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            try
            {
                _logger.LogInformation("Creating new subscription for User ID: {UserId}, Stripe ID: {StripeSubscriptionId}", subscription.UserId, subscription.StripeSubscriptionId);
                await _context.Subscriptions.AddAsync(subscription);
                await _context.SaveChangesAsync(); 
                _logger.LogInformation("Subscription created successfully with ID: {SubscriptionId}", subscription.Id);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error creating subscription for User ID: {UserId}, Stripe ID: {StripeSubscriptionId}", subscription.UserId, subscription.StripeSubscriptionId);
                throw;
            }
        }
        
        public async Task UpdateSubscriptionAsync(Subscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            try
            {
                _logger.LogInformation("Updating subscription ID: {SubscriptionId}, Stripe ID: {StripeSubscriptionId}", subscription.Id, subscription.StripeSubscriptionId);
                
                var existingSubscription = await _context.Subscriptions.FindAsync(subscription.Id);
                if (existingSubscription != null)
                {
                    _context.Entry(existingSubscription).CurrentValues.SetValues(subscription);
                }
                else
                {
                    _logger.LogWarning("Subscription with ID {SubscriptionId} not found for update. Attaching and marking as modified.", subscription.Id);
                    _context.Subscriptions.Update(subscription); 
                }
                await _context.SaveChangesAsync(); 
                _logger.LogInformation("Subscription updated successfully for ID: {SubscriptionId}", subscription.Id);

            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "Concurrency error updating subscription ID: {SubscriptionId}", subscription.Id);
                throw;
            }
            catch (DbUpdateException ex)
            {
                 _logger.LogError(ex, "Database error updating subscription ID: {SubscriptionId}", subscription.Id);
                throw;
            }
        }

        public async Task<bool> ValidateSubscriptionPriceAsync(string priceId, decimal expectedAmount)
        {
            if (string.IsNullOrEmpty(priceId))
            {
                _logger.LogWarning("ValidateSubscriptionPriceAsync called with null or empty priceId.");
                return false;
            }
            try
            {
                var plan = await _context.SubscriptionPlans
                                 .AsNoTracking()
                                 .FirstOrDefaultAsync(p => p.StripePriceId == priceId);
                if (plan == null)
                {
                    _logger.LogWarning("Subscription plan not found for Stripe Price ID: {StripePriceId}", priceId);
                    return false;
                }
                bool isValid = plan.Price == expectedAmount;
                if(!isValid)
                {
                    _logger.LogWarning("Subscription price mismatch for Stripe Price ID: {StripePriceId}. Expected: {ExpectedAmount}, Actual: {ActualPrice}", priceId, expectedAmount, plan.Price);
                }
                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating subscription price for Stripe Price ID: {StripePriceId}", priceId);
                return false; 
            }
        }

        public async Task<Payment?> GetPaymentByStripeSessionIdAsync(string stripeSessionId)
        {
            if (string.IsNullOrEmpty(stripeSessionId))
            {
                _logger.LogWarning("GetPaymentByStripeSessionIdAsync called with null or empty stripeSessionId.");
                return null;
            }
            _logger.LogInformation("Fetching payment by Stripe Session ID: {StripeSessionId}", stripeSessionId);
            try
            {
                return await _context.Payments 
                    .AsNoTracking()
                    .Include(p => p.Order) 
                    .FirstOrDefaultAsync(p => p.StripeSessionId == stripeSessionId);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Error fetching payment by Stripe Session ID: {StripeSessionId}", stripeSessionId);
                throw;
            }
        }
        
        // Implementation for GetPaymentByOrderIdAsync
        public async Task<Payment?> GetPaymentByOrderIdAsync(Guid orderId)
        {
            _logger.LogInformation("Fetching payment by Order ID: {OrderId}", orderId);
            try
            {
                return await _context.Payments
                    .AsNoTracking()
                    .Include(p => p.Order) // Optional: include order if needed directly
                    .FirstOrDefaultAsync(p => p.OrderId == orderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching payment by Order ID: {OrderId}", orderId);
                throw;
            }
        }

        public async Task CreatePaymentAsync(Payment payment)
        {
            if (payment == null) throw new ArgumentNullException(nameof(payment));
            try
            {
                _logger.LogInformation("Creating new payment for Order ID: {OrderId}, Stripe Session ID: {StripeSessionId}", payment.OrderId, payment.StripeSessionId);
                await _context.Payments.AddAsync(payment);
                await _context.SaveChangesAsync(); 
                _logger.LogInformation("Payment created successfully with ID: {PaymentId}", payment.Id);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error creating payment for Order ID: {OrderId}", payment.OrderId);
                throw;
            }
        }

        public async Task UpdatePaymentAsync(Payment payment)
        {
            if (payment == null) throw new ArgumentNullException(nameof(payment));
            try
            {
                _logger.LogInformation("Updating payment ID: {PaymentId}, Stripe Session ID: {StripeSessionId}", payment.Id, payment.StripeSessionId);
                var existingPayment = await _context.Payments.FindAsync(payment.Id);
                if (existingPayment != null)
                {
                    _context.Entry(existingPayment).CurrentValues.SetValues(payment);
                }
                else
                {
                    _logger.LogWarning("Payment with ID {PaymentId} not found for update. Attaching and marking as modified.", payment.Id);
                    _context.Payments.Update(payment);
                }
                await _context.SaveChangesAsync(); 
                _logger.LogInformation("Payment updated successfully for ID: {PaymentId}", payment.Id);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "Concurrency error updating payment ID: {PaymentId}", payment.Id);
                throw;
            }
            catch (DbUpdateException ex)
            {
                 _logger.LogError(ex, "Database error updating payment ID: {PaymentId}", payment.Id);
                throw;
            }
        }
        
        // Implementation for SaveGuestOrderInfoAsync
        public async Task SaveGuestOrderInfoAsync(GuestOrderInfo guestInfo)
        {
            if (guestInfo == null) throw new ArgumentNullException(nameof(guestInfo));
            try
            {
                _logger.LogInformation("Saving guest order info for Order ID: {OrderId}", guestInfo.OrderId);
                var existingInfo = await _context.GuestOrderInfos.FirstOrDefaultAsync(g => g.OrderId == guestInfo.OrderId);
                if (existingInfo != null)
                {
                    _logger.LogInformation("Updating existing guest order info for Order ID: {OrderId}", guestInfo.OrderId);
                    // Update all properties from the new guestInfo object
                    _context.Entry(existingInfo).CurrentValues.SetValues(guestInfo);
                    // Explicitly set OrderToken if it might not be part of SetValues or needs special handling
                    existingInfo.OrderToken = guestInfo.OrderToken; 
                }
                else
                {
                    await _context.GuestOrderInfos.AddAsync(guestInfo);
                }
                await _context.SaveChangesAsync();
                _logger.LogInformation("Guest order info saved successfully for Order ID: {OrderId}", guestInfo.OrderId);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error saving guest order info for Order ID: {OrderId}", guestInfo.OrderId);
                throw;
            }
        }

        // Implementation for SaveGuestOrderTokenAsync
        public async Task SaveGuestOrderTokenAsync(Guid orderId, string orderToken)
        {
            if (string.IsNullOrEmpty(orderToken)) throw new ArgumentException("Order token cannot be null or empty.", nameof(orderToken));
            try
            {
                _logger.LogInformation("Saving guest order token for Order ID: {OrderId}", orderId);
                var guestInfo = await _context.GuestOrderInfos.FirstOrDefaultAsync(g => g.OrderId == orderId);
                if (guestInfo != null)
                {
                    guestInfo.OrderToken = orderToken;
                    // _context.GuestOrderInfos.Update(guestInfo); // EF Core tracks changes on fetched entity
                }
                else
                {
                    // If GuestOrderInfo doesn't exist, create it.
                    _logger.LogWarning("GuestOrderInfo not found for Order ID {OrderId} when saving token. Creating new GuestOrderInfo record.", orderId);
                    guestInfo = new GuestOrderInfo { OrderId = orderId, OrderToken = orderToken };
                    await _context.GuestOrderInfos.AddAsync(guestInfo);
                }
                await _context.SaveChangesAsync();
                _logger.LogInformation("Guest order token saved successfully for Order ID: {OrderId}", orderId);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error saving guest order token for Order ID: {OrderId}", orderId);
                throw;
            }
        }

        // Implementation for GetGuestOrderInfoAsync
        public async Task<GuestOrderInfo?> GetGuestOrderInfoAsync(Guid orderId)
        {
            _logger.LogInformation("Fetching guest order info for Order ID: {OrderId}", orderId);
            try
            {
                return await _context.GuestOrderInfos
                    .AsNoTracking()
                    .FirstOrDefaultAsync(g => g.OrderId == orderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching guest order info for Order ID: {OrderId}", orderId);
                throw;
            }
        }

        public async Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return await _context.Database.BeginTransactionAsync(isolationLevel);
        }

        public async Task SaveChangesAsync()
        {
            try
            {
                _logger.LogInformation("Saving changes to the database.");
                await _context.SaveChangesAsync();
                _logger.LogInformation("Database changes saved successfully.");
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "A concurrency error occurred while saving changes to the database.");
                throw;
            }
            catch (DbUpdateException ex)
            {
                 _logger.LogError(ex, "A database update error occurred while saving changes.");
                throw;
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex, "An unexpected error occurred while saving changes to the database.");
                throw;
            }
        }
    }
}
