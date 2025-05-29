using System;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Stripe;
using Stripe.Checkout;
using ecommerce.Dto;
using ecommerce.Helpers;
using ecommerce.Models;
using ecommerce.Db;
using Microsoft.Extensions.Configuration;
using ecommerce.Infrastructure.Repository.Interfaces;
namespace ecommerce.Services
{
    public interface ISubscriptionProcessingService
    {
        Task<Session> CreateCheckoutSessionAsync(SubscriptionRequest request, string userId);
        Task ProcessCheckoutSessionAsync(Session session);
        Task ProcessSubscriptionUpdatedAsync(Stripe.Event stripeEvent);
        Task<SubscriptionStatusResponseDto> GetSubscriptionStatusAsync(string userId);
    }

    public class SubscriptionProcessingService : ISubscriptionProcessingService
    {
        private readonly IProductRepository _productRepository;
        private readonly IOrderRepository _orderRepository;
        private readonly IUserRepository _identityRepository;
        private readonly IStripeClient _stripeClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SubscriptionProcessingService> _logger;
        
        // Updated to use the correct Stripe.Subscription type
        private readonly AsyncRetryPolicy<Stripe.Subscription> _subscriptionRetryPolicy;

        public SubscriptionProcessingService(
            IProductRepository productRepository,
            IOrderRepository orderRepository,
            IUserRepository identityRepository,
            IStripeClient stripeClient,
            IConfiguration configuration,
            ILogger<SubscriptionProcessingService> logger)
        {
            _productRepository = productRepository;
            _orderRepository = orderRepository;
            _identityRepository = identityRepository;
            _stripeClient = stripeClient;
            _configuration = configuration;
            _logger = logger;

            _subscriptionRetryPolicy = Policy<Stripe.Subscription>
                .Handle<StripeException>()
                .WaitAndRetryAsync(3,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + 
                        TimeSpan.FromMilliseconds(new Random().Next(0, 100)),
                    onRetryAsync: async (outcome, delay, retryCount, context) =>
                    {
                        _logger.LogWarning(outcome.Exception, "Retry {RetryCount} for subscription retrieval after {Delay} seconds.", retryCount, delay.TotalSeconds);
                        await Task.CompletedTask;
                    });
        }

        public async Task<SubscriptionStatusResponseDto> GetSubscriptionStatusAsync(string userId)
        {
            var subscription = await _orderRepository.GetSubscriptionByUserIdAsync(userId);

            return new SubscriptionStatusResponseDto
            {
                IsSubscribed = subscription != null,
                PlanId = subscription?.PlanId ?? string.Empty,
                ExpiresAt = subscription?.ExpiresAt ?? DateTime.MinValue
            };
        }

        public async Task<Session> CreateCheckoutSessionAsync(SubscriptionRequest request, string userId)
        {
            bool isValidPrice = await _orderRepository.ValidateSubscriptionPriceAsync(request.PriceId, request.Amount);

            if (!isValidPrice)
            {
                _logger.LogWarning("Invalid PriceId '{PriceId}' for user {UserId}.", request.PriceId, userId);
                throw new InvalidOperationException("Invalid subscription plan.");
            }

            if (!Uri.TryCreate(request.RedirectPath, UriKind.Relative, out _))
            {
                _logger.LogWarning("Invalid redirect path: {RedirectPath}", request.RedirectPath);
                throw new InvalidOperationException("Invalid redirect path.");
            }

            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }
                },
                Mode = "subscription",
                SuccessUrl = $"{_configuration["Frontend:BaseUrl"]}/subscribe/success?session_id={{CHECKOUT_SESSION_ID}}",
                CancelUrl = $"{_configuration["Frontend:BaseUrl"]}/subscribe/cancel",
                Metadata = new Dictionary<string, string>
                {
                    { "user_id", userId },
                    { "signature", CryptoHelper.ComputeHMACSHA256(_configuration["Stripe:MetadataSignatureSecret"]!, userId) }
                }
            };

            var service = new SessionService(_stripeClient);
            string idempotencyKey = $"{userId}-{DateTime.UtcNow.Ticks}";
            var requestOptions = new RequestOptions { IdempotencyKey = idempotencyKey };
            return await service.CreateAsync(options, requestOptions);
        }

        public async Task ProcessCheckoutSessionAsync(Session session)
        {
            if (session?.SubscriptionId == null)
            {
                _logger.LogWarning("Checkout session missing SubscriptionId for session {SessionId}.", session?.Id);
                return;
            }

            var secret = _configuration["Stripe:MetadataSignatureSecret"];
            if (string.IsNullOrEmpty(secret))
            {
                _logger.LogError("Stripe:MetadataSignatureSecret is not configured.");
                throw new InvalidOperationException("Stripe metadata signature secret is not configured.");
            }

            if (!session.Metadata.TryGetValue("user_id", out string userIdString) || !ValidateMetadataSignature(session.Metadata, secret))
            {
                _logger.LogError("Metadata signature validation failed for session {SessionId}.", session.Id);
                throw new SecurityException("Invalid metadata signature.");
            }

            try
            {
                var subscriptionService = new SubscriptionService(_stripeClient);
                var stripeSubscription = await _subscriptionRetryPolicy.ExecuteAsync(() => subscriptionService.GetAsync(session.SubscriptionId));
                var price = stripeSubscription?.Items?.Data?.FirstOrDefault()?.Price;
                if (price == null)
                {
                    _logger.LogError("Missing price data in subscription for session {SessionId}.", session.Id);
                    return;
                }

                var subscriptionPlan = await _orderRepository.GetSubscriptionPlanByPriceIdAsync(price.Id);
                if (subscriptionPlan == null)
                {
                    _logger.LogError("Subscription plan not found for price {PriceId} in session {SessionId}.", price.Id, session.Id);
                    return;
                }

                var existingSubscription = await _orderRepository.GetSubscriptionByStripeIdAsync(stripeSubscription.Id);
                if (existingSubscription == null)
                {
                    var user = await _identityRepository.GetUserByIdAsync(userIdString);
                    if (user == null)
                    {
                        _logger.LogError("User not found for user_id {UserId}.", userIdString);
                        return;
                    }

                    var newSubscription = new ecommerce.Db.Subscription
                    {
                        UserId = user.Id,
                        StripeSubscriptionId = stripeSubscription.Id,
                        PlanId = subscriptionPlan?.Id.ToString() ?? string.Empty,
                        Status = MapSubscriptionStatus(stripeSubscription.Status),
                        ExpiresAt = stripeSubscription.CurrentPeriodEnd
                    };
                    await _orderRepository.CreateSubscriptionAsync(newSubscription);
                    _logger.LogInformation("Created new subscription with StripeSubscriptionId {SubscriptionId}.", stripeSubscription.Id);
                }
                else
                {
                    existingSubscription.Status = MapSubscriptionStatus(stripeSubscription.Status);
                    existingSubscription.ExpiresAt = stripeSubscription.CurrentPeriodEnd;
                    _logger.LogInformation("Updated subscription with StripeSubscriptionId {SubscriptionId}.", stripeSubscription.Id);
                }

                await _orderRepository.SaveChangesAsync();
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe API error processing subscription for session {SessionId}.", session.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing subscription for session {SessionId}.", session.Id);
                throw;
            }
        }

        public async Task ProcessSubscriptionUpdatedAsync(Stripe.Event stripeEvent)
        {
            var stripeSubscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (stripeSubscription?.Id == null)
            {
                _logger.LogWarning("Subscription ID missing in event {EventId}.", stripeEvent.Id);
                return;
            }

            try
            {
                var existingSubscription = await _orderRepository.GetSubscriptionByStripeIdAsync(stripeSubscription.Id);
                if (existingSubscription != null)
                {
                    existingSubscription.Status = MapSubscriptionStatus(stripeSubscription.Status);
                    existingSubscription.ExpiresAt = stripeSubscription.CurrentPeriodEnd;
                    await _orderRepository.SaveChangesAsync();
                    _logger.LogInformation("Updated subscription {SubscriptionId}.", stripeSubscription.Id);
                }
                else
                {
                    _logger.LogWarning("Subscription {SubscriptionId} not found in database.", stripeSubscription.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating subscription {SubscriptionId}.", stripeSubscription.Id);
                throw;
            }
        }

        private SubscriptionStatus MapSubscriptionStatus(string stripeStatus) => stripeStatus switch
        {
            "active" => SubscriptionStatus.Active,
            "canceled" => SubscriptionStatus.Canceled,
            "unpaid" => SubscriptionStatus.Expired,
            "past_due" => SubscriptionStatus.Expired,
            "trialing" => SubscriptionStatus.Trialing,
            "incomplete" => SubscriptionStatus.Incomplete,
            _ => SubscriptionStatus.Unknown
        };

        private bool ValidateMetadataSignature(IReadOnlyDictionary<string, string> metadata, string secret)
        {
            if (!metadata.ContainsKey("user_id") || !metadata.ContainsKey("signature"))
                return false;
            string userId = metadata["user_id"];
            string signature = metadata["signature"];
            var expectedSignature = CryptoHelper.ComputeHMACSHA256(secret, userId);
            bool isValid = signature == expectedSignature;
            if (!isValid)
                _logger.LogWarning("Metadata signature mismatch: received {ReceivedSignature}, expected {ExpectedSignature}.", signature, expectedSignature);
            return isValid;
        }
    }
}
