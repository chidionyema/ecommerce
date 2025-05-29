using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using ecommerce.Db;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace ecommerce.Tests
{
    public static class SignInManagerExtensions
    {
        // Use a static dictionary to store the login info per test
        private static readonly Dictionary<string, ExternalLoginInfo> _externalLoginInfoStore 
            = new Dictionary<string, ExternalLoginInfo>();

        public static void SetExternalLoginInfo(this SignInManager<User> signInManager, ExternalLoginInfo info)
        {
            // Use the sign-in manager instance as a key to store the login info
            var key = signInManager.GetHashCode().ToString();
            
            if (_externalLoginInfoStore.ContainsKey(key))
                _externalLoginInfoStore[key] = info;
            else
                _externalLoginInfoStore.Add(key, info);
        }

        // Override the GetExternalLoginInfoAsync method
        public static Task<ExternalLoginInfo> GetExternalLoginInfoAsync(this SignInManager<User> signInManager)
        {
            var key = signInManager.GetHashCode().ToString();
            
            if (_externalLoginInfoStore.TryGetValue(key, out var info))
                return Task.FromResult(info);
                
            return Task.FromResult<ExternalLoginInfo>(null);
        }
    }
}