namespace ecommerce.Models
{
    public class MinioSettings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string BucketName { get; set; }  = string.Empty;
        public bool Secure { get; set; } 
        public string AccessKey { get; set; }  = string.Empty;
        public string SecretKey { get; set; }  = string.Empty;
    }
}