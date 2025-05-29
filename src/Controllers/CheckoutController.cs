using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ecommerce.Dto;
using ecommerce.Services;

namespace ecommerce.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CheckoutController : ControllerBase
    {
        private readonly PaymentProcessingService _paymentService;
        private readonly ILogger<CheckoutController> _logger;

        public CheckoutController(PaymentProcessingService paymentService, ILogger<CheckoutController> logger)
        {
            _paymentService = paymentService;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> StartCheckout([FromBody] StartCheckoutRequest request)
        {
            string userId = User?.FindFirst("sub")?.Value ?? "guest";

            try
            {
                var (order, stripeSession) = await _paymentService.ProcessCheckoutAsync(request, userId);
                return Ok(new
                {
                    orderToken = stripeSession.Id,
                    sessionId = stripeSession.Id
                });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Checkout processing failed.");
                return StatusCode(500, new { message = "Checkout processing failed. Please try again." });
            }
        }

    }
}
