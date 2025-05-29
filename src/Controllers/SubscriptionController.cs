// File: ecommerce.Controllers/SubscriptionController.cs
using System; // Required for System.Exception
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ecommerce.Dto;
using ecommerce.Services; // Assuming ISubscriptionProcessingService is in this namespace
// Note: Stripe.Checkout.Session is used by the service, not directly in this controller usually for responses.
// If CreateCheckoutSessionResponseDto needs it, it would be via a property.

namespace ecommerce.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class SubscriptionController : ControllerBase
    {
        private readonly ISubscriptionProcessingService _subscriptionService;
        private readonly ILogger<SubscriptionController> _logger;

        public SubscriptionController(
            ISubscriptionProcessingService subscriptionService,
            ILogger<SubscriptionController> logger)
        {
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetSubscriptionStatus()
        {
            // User.FindFirst("sub") gets the subject claim, typically the user ID from the JWT.
            string? userId = User?.FindFirst("sub")?.Value; // "sub" is a standard claim for subject (user ID)

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetSubscriptionStatus: Unauthorized attempt. User ID claim ('sub') missing from token.");
                // Return 401 Unauthorized if the user ID cannot be determined from the token.
                // This might indicate an issue with token generation or an unauthenticated request
                // bypassing earlier auth mechanisms (though [Authorize] should handle that).
                return Unauthorized(new { message = "User ID could not be determined from the token." });
            }

            try
            {
                _logger.LogInformation("Retrieving subscription status for user {UserId}.", userId);
                var status = await _subscriptionService.GetSubscriptionStatusAsync(userId);
                _logger.LogInformation("Successfully retrieved subscription status for user {UserId}.", userId);
                return Ok(status);
            }
            catch (Exception ex) // Catches any exception from the service layer
            {
                _logger.LogError(ex, "Failed to retrieve subscription status for user {UserId}.", userId);
                // Return a generic 500 Internal Server Error response.
                return StatusCode(500, new { message = "An unexpected error occurred while retrieving subscription status." });
            }
        }

        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] SubscriptionRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { message = "Subscription request cannot be null." });
            }

            // Validate model state if you have annotations on SubscriptionRequest DTO
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            string? userId = User?.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("CreateCheckoutSession: Unauthorized attempt. User ID claim ('sub') missing from token.");
                return Unauthorized(new { message = "User ID could not be determined from the token." });
            }

            try
            {
                _logger.LogInformation("Creating checkout session for user {UserId} with PriceId {PriceId}.", userId, request.PriceId);
                // Assuming _subscriptionService.CreateCheckoutSessionAsync returns Stripe.Checkout.Session
                var stripeSession = await _subscriptionService.CreateCheckoutSessionAsync(request, userId);

                if (stripeSession == null || string.IsNullOrEmpty(stripeSession.Id))
                {
                    _logger.LogError("Stripe session creation returned null or empty Session ID for user {UserId} and PriceId {PriceId}.", userId, request.PriceId);
                    return StatusCode(500, new { message = "Failed to create checkout session with payment provider." });
                }

                _logger.LogInformation("Successfully created Stripe checkout session {StripeSessionId} for user {UserId}.", stripeSession.Id, userId);
                return Ok(new CreateCheckoutSessionResponseDto { SessionId = stripeSession.Id });
            }
            catch (InvalidOperationException ex) // Catch specific expected exceptions for bad client data
            {
                // This typically means a business rule was violated (e.g., invalid PriceId, bad redirect URL)
                // as determined by the SubscriptionProcessingService.
                _logger.LogWarning(ex, "Invalid operation while creating checkout session for user {UserId} with PriceId {PriceId}: {ErrorMessage}", userId, request.PriceId, ex.Message);
                return BadRequest(new { message = ex.Message }); // Return 400 Bad Request
            }
            catch (Stripe.StripeException ex) // Catch exceptions from the Stripe client library
            {
                _logger.LogError(ex, "Stripe API error while creating checkout session for user {UserId} with PriceId {PriceId}. Stripe Error Type: {StripeErrorType}, Code: {StripeErrorCode}, HTTP Status: {StripeHttpStatus}",
                    userId, request.PriceId, ex.StripeError?.Type, ex.StripeError?.Code, ex.HttpStatusCode);
                // You might want to return a more specific error or a generic one based on ex.HttpStatusCode or ex.StripeError.Code
                return StatusCode(500, new { message = "An error occurred with the payment provider. Please try again later." });
            }
            catch (Exception ex) // Generic catch-all for any other unexpected errors
            {
                _logger.LogError(ex, "An unexpected error occurred while creating checkout session for user {UserId} with PriceId {PriceId}.", userId, request.PriceId);
                return StatusCode(500, new { message = "An unexpected error occurred. Please try again later." });
            }
        }
    }
}