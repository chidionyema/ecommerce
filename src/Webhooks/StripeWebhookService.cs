using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using ecommerce.Services;

namespace ecommerce.Webhooks
{
    public class StripeWebhookService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<StripeWebhookService> _logger;

        public StripeWebhookService(IServiceProvider serviceProvider, ILogger<StripeWebhookService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<bool> ProcessWebhookEvent(Event stripeEvent)
        {
            try
            {
                switch (stripeEvent.Type)
                {
                    case "checkout.session.completed":
                        return await HandleCheckoutSessionCompleted(stripeEvent);
                    case "customer.subscription.updated":
                    case "customer.subscription.deleted":
                    case "customer.subscription.canceled":
                        return await HandleSubscriptionEvent(stripeEvent);
                    default:
                        _logger.LogInformation("Unhandled event type: {EventType}", stripeEvent.Type);
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Stripe event {EventId}", stripeEvent.Id);
                return false;
            }
        }

        private async Task<bool> HandleCheckoutSessionCompleted(Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Session;
            if (session == null)
            {
                _logger.LogWarning("Checkout session is null for event {EventId}", stripeEvent.Id);
                return false;
            }

            // Declare strategy as nullable since the default case may return null.
            ISessionHandlerStrategy? strategy = session.Mode switch
            {
                "subscription" => _serviceProvider.GetRequiredService<SubscriptionSessionStrategy>(),
                "payment" => _serviceProvider.GetRequiredService<PaymentSessionStrategy>(),
                _ => null
            };

            if (strategy == null)
            {
                _logger.LogWarning("Unhandled session mode: {Mode}", session.Mode);
                return false;
            }

            return await strategy.HandleSession(session);
        }

        private async Task<bool> HandleSubscriptionEvent(Event stripeEvent)
        {
            using var scope = _serviceProvider.CreateScope();
            var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionProcessingService>();
            await subscriptionService.ProcessSubscriptionUpdatedAsync(stripeEvent);
            return true;
        }
    }
}
