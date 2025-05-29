using System;
using System.Security.Claims;
using System.Threading.Tasks;
using ecommerce.Db;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ecommerce.Tests
{
    public static class TestAuthExtensions
    {


        public static IServiceCollection AddTestAuthentication(this IServiceCollection services)
        {
            // Replace the SignInManager with our test version that can return mock external login info
            services.Decorate<SignInManager<User>, TestSignInManager<User>>();
            
            // Register AuthService for tests
            services.AddScoped<ecommerce.Services.AuthService>();

            return services;
        }
    }

    // Thread-safe storage for the external login info during tests
    public static class ExternalLoginInfoStore
    {
        private static readonly System.Threading.AsyncLocal<ExternalLoginInfo> _currentLoginInfo = new();

        public static void SetLoginInfo(ExternalLoginInfo loginInfo)
        {
            _currentLoginInfo.Value = loginInfo;
        }

        public static ExternalLoginInfo GetLoginInfo()
        {
            return _currentLoginInfo.Value;
        }
    }

    // Custom TestSignInManager that overrides GetExternalLoginInfoAsync
    public class TestSignInManager<TUser> : SignInManager<TUser> where TUser : class
    {
        private readonly ILogger<SignInManager<TUser>> _logger;

        public TestSignInManager(
            UserManager<TUser> userManager,
            IHttpContextAccessor contextAccessor,
            IUserClaimsPrincipalFactory<TUser> claimsFactory,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<SignInManager<TUser>> logger,
            IAuthenticationSchemeProvider schemes,
            IUserConfirmation<TUser> confirmation)
            : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
        {
            _logger = logger;
        }

        public override Task<ExternalLoginInfo> GetExternalLoginInfoAsync(string expectedXsrf = null)
        {
            // Get the login info from our store
            var loginInfo = ExternalLoginInfoStore.GetLoginInfo();
            
            // If we have stored login info for this test, return it
            if (loginInfo != null)
            {
                _logger.LogInformation("TestSignInManager returning stored login info for provider: {Provider}", 
                    loginInfo.LoginProvider);
                return Task.FromResult(loginInfo);
            }
            
            // Otherwise, fall back to the base implementation
            return base.GetExternalLoginInfoAsync(expectedXsrf);
        }
    }
}