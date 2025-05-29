using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stripe.Checkout;
using ecommerce.Services;

namespace ecommerce.Webhooks
{
    public class SubscriptionSessionStrategy : ISessionHandlerStrategy
    {
        private readonly ISubscriptionProcessingService _subscriptionService;
        private readonly ILogger<SubscriptionSessionStrategy> _logger;

        public SubscriptionSessionStrategy(ISubscriptionProcessingService subscriptionService, ILogger<SubscriptionSessionStrategy> logger)
        {
            _subscriptionService = subscriptionService;
            _logger = logger;
        }

        public async Task<bool> HandleSession(Session session)
        {
            // Now the service expects a Session, not an Event.
            await _subscriptionService.ProcessCheckoutSessionAsync(session);
            _logger.LogInformation("Processed subscription session {SessionId}", session.Id);
            return true;
        }
    }
}
