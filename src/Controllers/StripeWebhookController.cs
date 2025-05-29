using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using ecommerce.Webhooks;
using Microsoft.AspNetCore.Authorization;

namespace ecommerce.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class StripeWebhookController: ControllerBase
    {
        private readonly StripeWebhookService _webhookService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(StripeWebhookService webhookService, IConfiguration configuration, ILogger<StripeWebhookController> logger)
        {
            _webhookService = webhookService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> HandleWebhook()
        {
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            // Convert StringValues to string before using null-coalescing operator
            string signatureHeader = Request.Headers["Stripe-Signature"].ToString()?? string.Empty;
            string endpointSecret = _configuration["Stripe:WebhookSecret"]?? string.Empty;

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, endpointSecret);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe webhook signature verification failed.");
                return BadRequest("Invalid signature.");
            }

            bool processed = await _webhookService.ProcessWebhookEvent(stripeEvent);
            if (processed)
            {
                return Ok();
            }
            else
            {
                return StatusCode(500, "Webhook processing failed.");
            }
        }
    }
}