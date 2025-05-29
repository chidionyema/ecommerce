using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stripe.Checkout;
using ecommerce.Services;

namespace ecommerce.Webhooks
{
    public class PaymentSessionStrategy : ISessionHandlerStrategy
    {
        private readonly IPaymentProcessingService _paymentService;
        private readonly ILogger<PaymentSessionStrategy> _logger;

        public PaymentSessionStrategy(IPaymentProcessingService paymentService, ILogger<PaymentSessionStrategy> logger)
        {
            _paymentService = paymentService;
            _logger = logger;
        }

        public async Task<bool> HandleSession(Session session)
        {
            await _paymentService.HandlePaymentSessionAsync(session);
            _logger.LogInformation("Processed payment session {SessionId}", session.Id);
            return true;
        }
    }
}
