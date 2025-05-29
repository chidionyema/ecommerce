using System.Threading.Tasks;
using Stripe.Checkout;

namespace ecommerce.Webhooks
{
    public interface ISessionHandlerStrategy
    {
        Task<bool> HandleSession(Session session);
    }
}
