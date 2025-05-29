// File: Controllers/Api/OrdersController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ecommerce.Db;
using System.Security.Claims;
using Stripe;
using Stripe.Checkout;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using ecommerce.Services;
using ecommerce.Infrastructure.Repository.Interfaces;

namespace ecommerce.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrdersController : ControllerBase // Removed [Authorize] to allow guest access
    {
        private readonly IOrderRepository _orderRepository;
        private readonly PaymentProcessingService _paymentService;
        private readonly ILogger<OrdersController> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _stripeSecretKey;
        private readonly string _csrfSecretKey;

        public OrdersController(
            IOrderRepository orderRepository,
            PaymentProcessingService paymentService,
            ILogger<OrdersController> logger,
            IConfiguration configuration)
        {
            _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
            _paymentService = paymentService;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _stripeSecretKey = _configuration["Stripe:SecretKey"] ?? throw new InvalidOperationException("Stripe:SecretKey is not configured");
            _csrfSecretKey = _configuration["Security:CsrfSecretKey"] ?? throw new InvalidOperationException("Security:CsrfSecretKey is not configured");
        }
        
        /// <summary>
        /// Gets all orders for the authenticated user
        /// </summary>
        [HttpGet]
        [Authorize] // Add Authorize attribute here for authenticated users only
        public async Task<ActionResult<IEnumerable<OrderResponseDto>>> GetUserOrders()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }
            
            try
            {
                // Get all orders from repository - we'll filter for the current user
                var allOrders = await _orderRepository.GetAllOrdersAsync();
                var userOrders = allOrders.Where(o => o.UserId == userId).ToList();
                
                // Map to response DTOs
                var response = userOrders.Select(o => new OrderResponseDto
                {
                    Id = o.Id,
                    OrderDate = o.CreatedAt,
                    Status = o.Status.ToString(),
                    TotalAmount = o.TotalAmount,
                    OrderItems = o.OrderItems?.Select(item => new OrderItemDto
                    {
                        ProductId = item.ProductId,
                        ProductName = item.Product?.Name ?? "Unknown Product",
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        ImageUrl = item.Product?.ImageUrl
                    }).ToList() ?? new List<OrderItemDto>()
                }).ToList();
                
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving orders for user {UserId}", userId);
                return StatusCode(500, new { error = "An error occurred while retrieving your orders" });
            }
        }

        /// <summary>
        /// Get order details by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<OrderResponseDto>> GetOrder(Guid id)
        {
            try
            {
                var order = await _orderRepository.GetOrderByIdAsync(id);
                if (order == null)
                {
                    return NotFound(new { error = "Order not found" });
                }
                
                // Check if user is authorized to view this order
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                
                // For guest orders, we need some other verification (like email + order token)
                var isGuestOrder = order.UserId == "guest";
                var guestOrderToken = Request.Headers["X-Guest-Order-Token"].FirstOrDefault();
                var guestEmail = Request.Headers["X-Guest-Email"].FirstOrDefault();
                
                // Verify the order belongs to the current user or is a valid guest request
                if (!isGuestOrder && (string.IsNullOrEmpty(userId) || order.UserId != userId))
                {
                    return Forbid();
                }
                
                // For guest orders, verify the order token and email match
                if (isGuestOrder)
                {
                    var guestInfo = await _orderRepository.GetGuestOrderInfoAsync(id);
                    if (guestInfo == null || guestInfo.Email != guestEmail || guestInfo.OrderToken != guestOrderToken)
                    {
                        return Forbid();
                    }
                }
                
                // Map to response DTO
                var response = new OrderResponseDto
                {
                    Id = order.Id,
                    OrderDate = order.CreatedAt,
                    Status = order.Status.ToString(),
                    TotalAmount = order.TotalAmount,
                    OrderItems = order.OrderItems?.Select(item => new OrderItemDto
                    {
                        ProductId = item.ProductId,
                        ProductName = item.Product?.Name ?? "Unknown Product",
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        ImageUrl = item.Product?.ImageUrl
                    }).ToList() ?? new List<OrderItemDto>()
                };
                
                // Get payment information if available
                var payment = await _orderRepository.GetPaymentByOrderIdAsync(id);
                if (payment != null)
                {
                    response.PaymentMethod = payment.PaymentMethod;
                    response.PaymentStatus = payment.Status.ToString();
                }
                
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving order {OrderId}", id);
                return StatusCode(500, new { error = "An error occurred while retrieving the order" });
            }
        }

        /// <summary>
        /// Verify order after checkout completion
        /// </summary>
        [HttpGet("verify")]
        public async Task<ActionResult<OrderResponseDto>> VerifyOrder([FromQuery] string? token, [FromQuery] string? sessionId)
        {
            if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "Either token or sessionId is required" });
            }
            
            try
            {
                // If we have a token, get order by token
                if (!string.IsNullOrEmpty(token))
                {
                    // Find order by token
                    // This would need additional implementation in the repository
                    return StatusCode(501, new { error = "Token-based order verification not implemented" });
                }
                
                // If we have a sessionId, get from Stripe and create/update order
                if (!string.IsNullOrEmpty(sessionId))
                {
                    // Configure Stripe API
                    StripeConfiguration.ApiKey = _stripeSecretKey;
                    
                    // Get session details from Stripe
                    var service = new Stripe.Checkout.SessionService();
                    Session session;
                    
                    try
                    {
                        session = await service.GetAsync(sessionId, new SessionGetOptions
                        {
                            Expand = new List<string> { "line_items", "payment_intent", "customer" }
                        });
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogError(ex, "Error retrieving Stripe session {SessionId}", sessionId);
                        return BadRequest(new { error = "Invalid session ID" });
                    }
                    
                    // Check if session is paid
                    if (session.PaymentStatus != "paid")
                    {
                        return Ok(new
                        {
                            status = "pending",
                            message = "Payment has not been completed yet"
                        });
                    }
                    
                    // Get existing payment from database
                    var payment = await _orderRepository.GetPaymentByStripeSessionIdAsync(sessionId);
                    
                    // If no payment record exists, this is an unexpected state
                    if (payment == null)
                    {
                        _logger.LogWarning("No payment record found for completed Stripe session {SessionId}", sessionId);
                        return BadRequest(new { error = "Payment record not found" });
                    }
                    
                    // For security, validate the session against our internal records
                    // This helps prevent session forgery attacks
                    if (!await ValidateStripeSession(session, payment))
                    {
                        _logger.LogWarning("Stripe session validation failed for session {SessionId}", sessionId);
                        return BadRequest(new { error = "Invalid checkout session" });
                    }
                    
                    // Get the associated order
                    var order = await _orderRepository.GetOrderByIdAsync(payment.OrderId);
                    if (order == null)
                    {
                        _logger.LogError("Order not found for payment {PaymentId}", payment.Id);
                        return BadRequest(new { error = "Order not found for the provided session" });
                    }
                    
                    // Start a transaction for updating payment and order status
                    using var transaction = await _orderRepository.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                    
                    try
                    {
                        // Update payment status
                        payment.Status = PaymentStatus.Completed;
                        payment.StripePaymentIntentId = session.PaymentIntentId;
                        payment.LastUpdatedAtUtc = DateTime.UtcNow;
                        await _orderRepository.UpdatePaymentAsync(payment);
                        
                        // Update order status
                        order.Status = OrderStatus.Completed;
                        await _orderRepository.UpdateOrderStatusAsync(order.Id, OrderStatus.Completed);
                        
                        // Commit transaction
                        await transaction.CommitAsync();
                        
                        // Generate a secure order token for guest users to access their order details
                        string? orderToken = null;
                        if (order.UserId == "guest")
                        {
                            orderToken = GenerateOrderToken(order.Id);
                            // Save this token in your database associated with the order
                            await _orderRepository.SaveGuestOrderTokenAsync(order.Id, orderToken);
                        }
                        
                        // Build response
                        var response = new OrderResponseDto
                        {
                            Id = order.Id,
                            OrderDate = order.CreatedAt,
                            Status = order.Status.ToString(),
                            TotalAmount = order.TotalAmount,
                            OrderItems = order.OrderItems?.Select(item => new OrderItemDto
                            {
                                ProductId = item.ProductId,
                                ProductName = item.Product?.Name ?? "Unknown Product",
                                Quantity = item.Quantity,
                                UnitPrice = item.UnitPrice,
                                ImageUrl = item.Product?.ImageUrl
                            }).ToList() ?? new List<OrderItemDto>(),
                            PaymentMethod = payment.PaymentMethod,
                            PaymentStatus = payment.Status.ToString(),
                            OrderToken = orderToken // Include the token for guest users
                        };
                        
                        return Ok(response);
                    }
                    catch (Exception ex)
                    {
                        // Rollback transaction
                        await transaction.RollbackAsync();
                        _logger.LogError(ex, "Error updating order and payment status");
                        throw;
                    }
                }
                
                return BadRequest(new { error = "Invalid request parameters" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying order");
                return StatusCode(500, new { error = "An error occurred while verifying the order" });
            }
        }
        
        // Helper method to validate a Stripe checkout session
        private async Task<bool> ValidateStripeSession(Session session, Payment payment)
        {
            // Check that session amount matches payment amount
            if (session.AmountTotal != (long)(payment.Amount * 100)) // Convert to cents for Stripe
            {
                return false;
            }
            
            // Check that the session was created within a reasonable time frame
            var sessionCreatedAt = session.Created;
            var paymentCreatedAt = payment.CreatedAt;
            if (Math.Abs((sessionCreatedAt - paymentCreatedAt.ToUniversalTime()).TotalMinutes) > 30)
            {
                return false;
            }
            
            // Additional checks could be implemented based on your business needs
            
            return true;
        }
        
        // Generate a secure token for guest orders
        private string GenerateOrderToken(Guid orderId)
        {
            // Create a unique token that includes the order ID and a timestamp
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = $"{orderId}:{timestamp}:{_csrfSecretKey}";
            
            // Create a hash of the data for security
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(_csrfSecretKey));
            var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            
            // Return the token in a URL-friendly format
            return $"{Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').Replace("=", "")}:{timestamp}";
        }
        
        /// <summary>
        /// Create a checkout session for the specified items
        /// </summary>
        [HttpPost("checkout")]
        [ValidateAntiForgeryToken] // CSRF protection
        public async Task<ActionResult<CheckoutResponseDto>> CreateCheckout(CheckoutRequestDto request)
        {
            // Validate CSRF token from header
            var csrfToken = Request.Headers["X-CSRF-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(csrfToken))
            {
                return BadRequest(new { error = "CSRF token is required" });
            }
            
            // Validate request
            if (request == null || request.Items == null || !request.Items.Any())
            {
                return BadRequest(new { error = "No items provided for checkout" });
            }
            
            // Determine user ID - either from authentication or use "guest" for guest checkout
            string userId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "guest";
            
            // If guest checkout, validate guest information
            if (userId == "guest" && request.IsGuest)
            {
                if (request.GuestInfo == null)
                {
                    return BadRequest(new { error = "Guest checkout requires shipping information" });
                }
                
                // Validate essential guest information
                if (string.IsNullOrEmpty(request.GuestInfo.Email) || 
                    string.IsNullOrEmpty(request.GuestInfo.FirstName) || 
                    string.IsNullOrEmpty(request.GuestInfo.LastName) || 
                    string.IsNullOrEmpty(request.GuestInfo.Address) || 
                    string.IsNullOrEmpty(request.GuestInfo.City) || 
                    string.IsNullOrEmpty(request.GuestInfo.PostalCode) || 
                    string.IsNullOrEmpty(request.GuestInfo.Country))
                {
                    return BadRequest(new { error = "Incomplete guest information provided" });
                }
                
                // Validate email format
                if (!IsValidEmail(request.GuestInfo.Email))
                {
                    return BadRequest(new { error = "Invalid email format" });
                }
            }

            try
            {
                // Create a starting checkout request for the payment service
                var startCheckoutRequest = new StartCheckoutRequest
                {
                    Items = request.Items.Select(item => new CheckoutItemModel
                    {
                        ProductId = item.ProductId,
                        Quantity = item.Quantity
                    }).ToList(),
                    RedirectPath = request.RedirectPath
                };
                
                // If guest checkout, add guest information
                if (userId == "guest" && request.IsGuest && request.GuestInfo != null)
                {
                    startCheckoutRequest.GuestInfo = new GuestInfoModel
                    {
                        Email = request.GuestInfo.Email,
                        FirstName = request.GuestInfo.FirstName,
                        LastName = request.GuestInfo.LastName,
                        Address = request.GuestInfo.Address,
                        City = request.GuestInfo.City,
                        State = request.GuestInfo.State,
                        PostalCode = request.GuestInfo.PostalCode,
                        Country = request.GuestInfo.Country,
                        Phone = request.GuestInfo.Phone
                    };
                }
                
                // Process the checkout
                var (order, stripeSession) = await _paymentService.ProcessCheckoutAsync(startCheckoutRequest, userId);
                
                // Prepare response
                var response = new CheckoutResponseDto
                {
                    OrderId = order.Id,
                    SessionId = stripeSession.Id
                };
                
                // Add guest token if applicable
                if (userId == "guest")
                {
                    response.GuestOrderToken = GenerateOrderToken(order.Id);
                    
                    // Store guest information with the order
                    if (request.GuestInfo != null)
                    {
                        await _orderRepository.SaveGuestOrderInfoAsync( new GuestOrderInfo
                        {
                            OrderId = order.Id,
                            Email = request.GuestInfo.Email,
                            FirstName = request.GuestInfo.FirstName,
                            LastName = request.GuestInfo.LastName,
                            Address = request.GuestInfo.Address,
                            City = request.GuestInfo.City,
                            State = request.GuestInfo.State,
                            PostalCode = request.GuestInfo.PostalCode,
                            Country = request.GuestInfo.Country,
                            Phone = request.GuestInfo.Phone,
                            OrderToken = response.GuestOrderToken
                        });
                    }
                }
                
                return Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Checkout validation failed");
                return BadRequest(new { error = ex.Message });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Checkout processing failed");
                return StatusCode(500, new { error = "Checkout processing failed. Please try again." });
            }
        }
        
        // Helper to validate email format
        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }

    // Request and Response DTOs
    
    public class CheckoutRequestDto
    {
        [Required]
        public List<CheckoutItemDto> Items { get; set; } = new List<CheckoutItemDto>();

        [JsonIgnore]
        public string? RedirectPath { get; set; }
        
        public bool IsGuest { get; set; }
        
        public GuestInfoDto? GuestInfo { get; set; }
    }
    
    public class GuestInfoDto
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

    public class CheckoutItemDto
    {
        [Required]
        public Guid ProductId { get; set; }

        [Required]
        public string ProductName { get; set; } = string.Empty;

        [Required]
        [Range(1, int.MaxValue)]
        public int Quantity { get; set; }

        [Required]
        [Range(0.01, double.MaxValue)]
        public decimal UnitPrice { get; set; }

        public string? ImageUrl { get; set; }
    }

    public class CheckoutResponseDto
    {
        public Guid OrderId { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string? GuestOrderToken { get; set; }
    }

    public class OrderResponseDto
    {
        public Guid Id { get; set; }
        public DateTime OrderDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public List<OrderItemDto> OrderItems { get; set; } = new List<OrderItemDto>();
        public string? PaymentMethod { get; set; }
        public string? PaymentStatus { get; set; }
        public string? OrderToken { get; set; }
    }

    public class OrderItemDto
    {
        public Guid ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? ImageUrl { get; set; }
    }
}