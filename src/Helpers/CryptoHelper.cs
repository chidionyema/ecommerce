using System.Security.Cryptography;
using System.Text;
using System;

namespace ecommerce.Helpers
{
    public static class CryptoHelper
    {
        public static string ComputeHMACSHA256(string key, string data)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }
}
