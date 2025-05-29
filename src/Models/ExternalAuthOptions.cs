namespace ecommerce.Models
{
    public class ExternalAuthOptions
    {
        public GoogleAuthOptions Google { get; set; } = new GoogleAuthOptions();
        public MicrosoftAuthOptions Microsoft { get; set; } = new MicrosoftAuthOptions();
        public FacebookAuthOptions Facebook { get; set; } = new FacebookAuthOptions();
    }

    public class GoogleAuthOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }

    public class MicrosoftAuthOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }

    public class FacebookAuthOptions
    {
        public string AppId { get; set; } = string.Empty;
        public string AppSecret { get; set; } = string.Empty;
    }
}