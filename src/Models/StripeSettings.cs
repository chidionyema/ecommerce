namespace ecommerce.Models
{
    public class StripeSettings
    {
        public string SecretKey { get; set; } = string.Empty;
        public string PublishableKey { get; set; }  = string.Empty;
        public string WebhookSecret { get; internal set; } = string.Empty;
    }
}