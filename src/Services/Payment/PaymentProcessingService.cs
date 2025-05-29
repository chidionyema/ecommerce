using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using ecommerce.Contracts;
using ecommerce.Db;
using ecommerce.Dto;
using ecommerce.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Stripe;
using Stripe.Checkout;
using ecommerce.Infrastructure.Repository.Interfaces;


namespace ecommerce.Services
{
    public interface IPaymentProcessingService
    {
        Task<(Order order, Session stripeSession)> ProcessCheckoutAsync(StartCheckoutRequest request, string userId);
        Task HandlePaymentSessionAsync(Session session);
        Task<bool> ValidateCheckoutSessionAsync(string sessionId, string userId);
    }

    public class PaymentProcessingService : IPaymentProcessingService
    {
        private readonly IOrderRepository _orderRepository;
        private readonly IProductRepository _productRepository;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PaymentProcessingService> _logger;
        private readonly ITelemetryService _telemetry;
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _cache;

        public PaymentProcessingService(
            IOrderRepository orderRepository,
            IProductRepository productRepository,
            IConfiguration configuration,
            ILogger<PaymentProcessingService> logger,
            ITelemetryService telemetry,
            IDataProtectionProvider protectionProvider,
            IMemoryCache cache)
        {
            _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
            _productRepository = productRepository ?? throw new ArgumentNullException(nameof(productRepository));
            _configuration = configuration;
            _logger = logger;
            _telemetry = telemetry;
            _protector = protectionProvider.CreateProtector("Checkout.OrderId");
            _cache = cache;
        }

        public async Task<(Order order, Session stripeSession)> ProcessCheckoutAsync(StartCheckoutRequest request, string userId)
        {
            // Generate idempotency key to prevent duplicate orders
            var idempotencyKey = GenerateIdempotencyKey(userId, request);

            // Begin transaction to ensure data consistency
            await using var transaction = await _orderRepository.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            
            // Check for duplicate orders
            if (await _orderRepository.GetOrderByIdempotencyKeyAsync(idempotencyKey) is { } duplicate)
            {
                _logger.LogInformation("Duplicate order detected: {Key}", idempotencyKey);
                throw new InvalidOperationException("Duplicate order detected.");
            }

            // Fetch product data and validate availability
            var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _productRepository.GetProductsByIdsAsync(productIds);

            // Validate stock availability for all items
            foreach (var item in request.Items)
            {
                var product = products.FirstOrDefault(p => p.Id == item.ProductId);
                if (product == null)
                {
                    throw new InvalidOperationException($"Product {item.ProductId} not found");
                }
                
                if (!await _productRepository.ValidateStockAsync(item.ProductId, item.Quantity))
                {
                    throw new InvalidOperationException($"Insufficient stock for product {item.ProductId}");
                }
            }

            // Calculate order totals
            var totalAmount = request.Items.Sum(i => 
            {
                var product = products.First(p => p.Id == i.ProductId);
                return product.UnitPrice * i.Quantity;
            });
            
            var tax = totalAmount * _configuration.GetValue<decimal>("TaxRate", 0.0m);

            // Create order record
            var order = new Order(
                id: Guid.NewGuid(),
                totalAmount: totalAmount + tax,
                status: OrderStatus.Pending,
                userId: userId)
            {
                IdempotencyKey = idempotencyKey,
                OrderItems = request.Items.Select(i => new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = products.First(p => p.Id == i.ProductId).UnitPrice
                }).ToList()
            };

            // Save guest information if available
            if (userId == "guest" && request.GuestInfo != null)
            {
                order.GuestEmail = request.GuestInfo.Email;
                order.GuestFirstName = request.GuestInfo.FirstName;
                order.GuestLastName = request.GuestInfo.LastName;
            }

            // Save order to database
            await _orderRepository.CreateOrderAsync(order);

            // Create payment record
            var paymentRecord = new Payment
            {
                Id = Guid.NewGuid(),
                OrderDate = DateTime.UtcNow,
                UserId = userId,
                OrderId = order.Id,
                Amount = totalAmount,
                Tax = tax,
                IsComplete = false,
                Status = PaymentStatus.Pending,
                StripeSessionId = null,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAtUtc = DateTime.UtcNow
            };

            // Save payment record
            await _orderRepository.CreatePaymentAsync(paymentRecord);
            
            // Commit transaction for order and payment creation
            await transaction.CommitAsync();

            // Get frontend URLs from configuration
            var frontendBaseUrl = _configuration["Frontend:BaseUrl"];
            var successUrl = $"{frontendBaseUrl}/store/checkout/success?sessionId={{CHECKOUT_SESSION_ID}}";
            var cancelUrl = $"{frontendBaseUrl}/store/checkout/error?code=payment_canceled";
            
            if (!string.IsNullOrEmpty(request.RedirectPath))
            {
                successUrl = $"{frontendBaseUrl}{request.RedirectPath}?sessionId={{CHECKOUT_SESSION_ID}}";
            }

            // Initialize Stripe for checkout
            var stripeClient = new StripeClient(_configuration["Stripe:SecretKey"]);
            var sessionService = new SessionService(stripeClient);
            
            // Create checkout session options
            var sessionOptions = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = request.Items.Select(item =>
                {
                    var product = products.First(p => p.Id == item.ProductId);
                    return new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = "usd",
                            ProductData = new SessionLineItemPriceDataProductDataOptions 
                            { 
                                Name = product.Name,
                                // Add images if available
                                Images = product.ImageUrl != null ? new List<string> { product.ImageUrl } : null
                            },
                            UnitAmount = (long)Math.Round(product.UnitPrice * 100, MidpointRounding.AwayFromZero)
                        },
                        Quantity = item.Quantity
                    };
                }).ToList(),
                Mode = "payment",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                // Add customer email if guest checkout
                CustomerEmail = userId == "guest" && request.GuestInfo != null ? request.GuestInfo.Email : null,
                // Add metadata for webhook processing
                Metadata = new Dictionary<string, string>
                {
                    ["orderId"] = order.Id.ToString(),
                    ["userId"] = userId,
                    ["isGuest"] = (userId == "guest").ToString().ToLower()
                },
                // Enable automatic tax calculation if configured
                AutomaticTax = new SessionAutomaticTaxOptions
                {
                    Enabled = _configuration.GetValue<bool>("Stripe:EnableAutomaticTax", false)
                }
            };

            // Customer shipping address for tax calculation and receipt
            if (request.GuestInfo != null)
            {
                sessionOptions.ShippingAddressCollection = new SessionShippingAddressCollectionOptions
                {
                    AllowedCountries = new List<string> { "US", "CA", "GB", "AU" } // Configurable
                };
                
                // Pre-fill shipping address if available
                if (!string.IsNullOrEmpty(request.GuestInfo.Address))
                {
                    // Just set customer email - API doesn't support CustomerDetails
                    sessionOptions.CustomerEmail = request.GuestInfo.Email;
                    
                    // Store shipping info in metadata instead
                    sessionOptions.Metadata["customer_name"] = $"{request.GuestInfo.FirstName} {request.GuestInfo.LastName}";
                    sessionOptions.Metadata["shipping_address"] = request.GuestInfo.Address;
                    sessionOptions.Metadata["shipping_city"] = request.GuestInfo.City;
                    sessionOptions.Metadata["shipping_state"] = request.GuestInfo.State;
                    sessionOptions.Metadata["shipping_postal"] = request.GuestInfo.PostalCode;
                    sessionOptions.Metadata["shipping_country"] = request.GuestInfo.Country;
                    
                    // Enable billing address collection
                    sessionOptions.BillingAddressCollection = "required";
                }
            }

            // Use idempotency key to prevent duplicate sessions
            var requestOptions = new RequestOptions { IdempotencyKey = idempotencyKey };
            var stripeSession = await sessionService.CreateAsync(sessionOptions, requestOptions);

            // Update payment record with session ID
            await using var updateTransaction = await _orderRepository.BeginTransactionAsync();
            paymentRecord.StripeSessionId = stripeSession.Id;
            await _orderRepository.UpdatePaymentAsync(paymentRecord);
            await updateTransaction.CommitAsync();

            // Cache session data for validation
            _cache.Set($"checkout_session:{stripeSession.Id}", 
                new { OrderId = order.Id, UserId = userId, Amount = totalAmount, Created = DateTime.UtcNow },
                TimeSpan.FromHours(24));

            // Track event for analytics
            _telemetry.TrackEvent("StripeSessionCreated", new Dictionary<string, string>
            {
                ["OrderId"] = order.Id.ToString(),
                ["Amount"] = paymentRecord.Amount.ToString("C"),
                ["UserType"] = userId == "guest" ? "Guest" : "Registered"
            });

            return (order, stripeSession);
        }

        public async Task HandlePaymentSessionAsync(Session session)
        {
            // Retrieve existing payment record
            var paymentRecord = await _orderRepository.GetPaymentByStripeSessionIdAsync(session.Id);
            if (paymentRecord == null)
            {
                _logger.LogWarning("Payment record not found for session {SessionId}", session.Id);
                throw new InvalidOperationException($"Payment record not found for session {session.Id}");
            }

            // Validate session against cached data
            var cachedSession = _cache.Get<dynamic>($"checkout_session:{session.Id}");
            if (cachedSession == null || cachedSession.OrderId.ToString() != paymentRecord.OrderId.ToString())
            {
                _logger.LogWarning("Session validation failed for {SessionId}", session.Id);
                throw new InvalidOperationException("Session validation failed");
            }

            // Start transaction for updating order and inventory
            await using var transaction = await _orderRepository.BeginTransactionAsync();
            
            try
            {
                // Update payment status
                paymentRecord.Status = PaymentStatus.Completed;
                paymentRecord.IsComplete = true;
                paymentRecord.LastUpdatedAtUtc = DateTime.UtcNow;
                paymentRecord.StripePaymentIntentId = session.PaymentIntentId;
                await _orderRepository.UpdatePaymentAsync(paymentRecord);

                // Update order status
                var order = await _orderRepository.GetOrderByIdAsync(paymentRecord.OrderId);
                if (order == null)
                    throw new InvalidOperationException($"Order not found for ID {paymentRecord.OrderId}");
                
                order.Status = OrderStatus.Completed;
                await _orderRepository.UpdateOrderStatusAsync(order.Id, order.Status);

                // Update inventory for each item
                foreach (var item in order.OrderItems!)
                {
                    bool updated = await _productRepository.DecrementStockAsync(item.ProductId, item.Quantity);
                    if (!updated)
                    {
                        // Handle stock issues gracefully
                        _logger.LogWarning("Stock update failed for product {ProductId}", item.ProductId);
                        // Consider flagging the order for manual review instead of failing
                        order.Status = OrderStatus.RequiresReview;
                        await _orderRepository.UpdateOrderStatusAsync(order.Id, order.Status);
                    }
                }

                // Commit all changes
                await transaction.CommitAsync();
                
                // Remove from cache after successful processing
                _cache.Remove($"checkout_session:{session.Id}");
                
                _logger.LogInformation("Successfully processed payment session {SessionId} for order {OrderId}", 
                    session.Id, paymentRecord.OrderId);
            }
            catch (Exception ex)
            {
                // Roll back transaction on error
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error processing payment session {SessionId}", session.Id);
                throw;
            }
        }

        public async Task<bool> ValidateCheckoutSessionAsync(string sessionId, string userId)
        {
            // Check cache first
            var cachedSession = _cache.Get<dynamic>($"checkout_session:{sessionId}");
            if (cachedSession != null)
            {
                // For guest users, we don't need to match userId
                if (userId == "guest" || cachedSession.UserId == userId)
                {
                    // Verify the session isn't too old (24 hours max)
                    var created = (DateTime)cachedSession.Created;
                    if (DateTime.UtcNow.Subtract(created).TotalHours <= 24)
                    {
                        return true;
                    }
                }
                return false;
            }

            // If not in cache, check database
            var payment = await _orderRepository.GetPaymentByStripeSessionIdAsync(sessionId);
            if (payment == null)
            {
                return false;
            }

            // For guest users, we don't need to match userId
            if (userId != "guest" && payment.UserId != userId)
            {
                return false;
            }

            // Verify with Stripe API
            try
            {
                StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
                var service = new SessionService();
                var session = await service.GetAsync(sessionId);
                
                // Session must be completed
                if (session.Status != "complete")
                {
                    return false;
                }
                
                // Session must be paid
                if (session.PaymentStatus != "paid")
                {
                    return false;
                }
                
                // Session must be for the correct order
                if (session.Metadata != null && 
                    session.Metadata.TryGetValue("orderId", out var orderId) && 
                    orderId != payment.OrderId.ToString())
                {
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating session {SessionId}", sessionId);
                return false;
            }
        }

        private string GenerateIdempotencyKey(string userId, StartCheckoutRequest request)
        {
            var payload = new
            {
                UserId = userId,
                Items = request.Items.OrderBy(i => i.ProductId).Select(i => new { i.ProductId, i.Quantity }),
                Timestamp = DateTime.UtcNow.Ticks
            };
            
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            return Convert.ToBase64String(hash);
        }

        private string GenerateSuccessToken(Guid orderId) => _protector.Protect(orderId.ToString());
    }

    public class StartCheckoutRequest
    {
        public List<CheckoutItemModel> Items { get; set; } = new List<CheckoutItemModel>();
        public string? RedirectPath { get; set; }
        public GuestInfoModel? GuestInfo { get; set; }
    }

    public class CheckoutItemModel
    {
        public Guid ProductId { get; set; }
        public int Quantity { get; set; }
    }

    public class GuestInfoModel
    {
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string? Phone { get; set; }
    }
}