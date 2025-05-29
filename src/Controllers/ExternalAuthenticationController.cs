using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using ecommerce.Models;
using ecommerce.Db;
using ecommerce.Services;


namespace ecommerce.Controllers
{
    [ApiController]
    [Route("api/external-authentication")]
    public class ExternalAuthenticationController : ControllerBase
    {
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _userManager;
        private readonly ILogger<ExternalAuthenticationController> _logger;
        private readonly ecommerceContext _ecommerceContext;
        private readonly AuthService _authService;

        public ExternalAuthenticationController(
            SignInManager<User> signInManager,
            UserManager<User> userManager,
            ILogger<ExternalAuthenticationController> logger,
            ecommerceContext ecommerceContext,
            AuthService authService)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _logger = logger;
            _ecommerceContext = ecommerceContext;
            _authService = authService;
        }

        // Triggers the OAuth challenge flow
        [HttpGet("challenge/{provider}")]
        public IActionResult Challenge(string provider, [FromQuery] string redirectUrl)
        {
            // Check if the provider exists
            var providers = _signInManager.GetExternalAuthenticationSchemesAsync().Result;
            if (!providers.Any(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Invalid authentication provider requested: {Provider}", provider);
                return BadRequest($"Provider '{provider}' is not supported");
            }

            if (string.IsNullOrEmpty(redirectUrl))
            {
                redirectUrl = Url.Action(nameof(Callback), "ExternalAuthentication");
            }

            var properties = _signInManager.ConfigureExternalAuthenticationProperties(
                provider, redirectUrl);
            
            return Challenge(properties, provider);
        }

        // Callback endpoint for the OAuth provider to return to
        [HttpGet("callback")]
        public async Task<IActionResult> Callback()
        {
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                _logger.LogWarning("External login info is null");
                return BadRequest("Error getting external login information");
            }

            _logger.LogInformation("Received external login for provider: {Provider}, Key: {ProviderKey}",
                info.LoginProvider, info.ProviderKey);

            // Try to sign in with the external login
            var signInResult = await _signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);

            if (signInResult.Succeeded)
            {
                // User is already registered with this external login
                _logger.LogInformation("User signed in with external provider: {Provider}", info.LoginProvider);
                
                // Get the existing user
                var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                if (user == null)
                {
                    _logger.LogError("User found via ExternalLoginSignInAsync but not via FindByLoginAsync");
                    return BadRequest("User account inconsistency");
                }

                // Generate the token and return
                return await GenerateAuthResponseForUser(user);
            }
            else
            {
                // User is not registered with this external login
                // Try to find if the email is already registered
                var email = info.Principal.FindFirstValue(ClaimTypes.Email);
                if (string.IsNullOrEmpty(email))
                {
                    _logger.LogWarning("No email claim found from external provider");
                    return BadRequest("Email not provided by external login provider");
                }

                // Check if user with this email already exists
                var user = await _userManager.FindByEmailAsync(email);
                if (user != null)
                {
                    // Add this external login to the existing user
                    var addLoginResult = await _userManager.AddLoginAsync(user, info);
                    if (!addLoginResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to add external login to existing user: {Email}, Errors: {Errors}",
                            email, string.Join(", ", addLoginResult.Errors.Select(e => e.Description)));
                        return BadRequest("Could not associate external login with your account");
                    }

                    _logger.LogInformation("Added external login to existing user: {Email}", email);
                    return await GenerateAuthResponseForUser(user);
                }
                else
                {
                    // Create a new user with this external login
                    var name = info.Principal.FindFirstValue(ClaimTypes.Name) ?? email.Split('@')[0];
                    
                    user = new User
                    {
                        UserName = name,
                        Email = email,
                        EmailConfirmed = true // We trust the email from OAuth provider
                    };

                    var createResult = await _userManager.CreateAsync(user);
                    if (!createResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to create user from external login: {Errors}",
                            string.Join(", ", createResult.Errors.Select(e => e.Description)));
                        return BadRequest("Could not create account from external login");
                    }

                    var addLoginResult = await _userManager.AddLoginAsync(user, info);
                    if (!addLoginResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to add external login to new user: {Email}, Errors: {Errors}",
                            email, string.Join(", ", addLoginResult.Errors.Select(e => e.Description)));
                        
                        // Clean up the user we just created since we couldn't add the login
                        await _userManager.DeleteAsync(user);
                        return BadRequest("Could not associate external login with new account");
                    }

                    // Assign default role
                    var roleResult = await _userManager.AddToRoleAsync(user, "ContentUploader");
                    if (!roleResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to add role to new user: {Email}, Errors: {Errors}",
                            email, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    }

                    var claimResult = await _userManager.AddClaimAsync(
                        user, new Claim("permission", "upload_content")
                    );
                    if (!claimResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to add claims to new user: {Email}, Errors: {Errors}",
                            email, string.Join(", ", claimResult.Errors.Select(e => e.Description)));
                    }

                    _logger.LogInformation("Created new user from external login: {Email}", email);
                    return await GenerateAuthResponseForUser(user);
                }
            }
        }

        // Helper method to generate token and cookie for authentication response
        private async Task<IActionResult> GenerateAuthResponseForUser(User user)
        {
            var token = await _authService.GenerateToken(user, DateTime.UtcNow.AddMinutes(15));
            var refreshTokenEntity = await _authService.GenerateRefreshToken(user.Id);
            _authService.SetSecureCookie(HttpContext, token);

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                refreshToken = refreshTokenEntity.Token,
                user = new
                {
                    id = user.Id,
                    userName = user.UserName,
                    email = user.Email
                },
                expires = token.ValidTo
            });
        }

        [HttpGet("providers")]
        public async Task<IActionResult> GetAvailableProviders()
        {
            var providers = (await _signInManager.GetExternalAuthenticationSchemesAsync())
                    .Select(p => new { p.Name, p.DisplayName })
                    .ToList();        
            return Ok(new { providers });
        }

        [Authorize]
        [HttpPost("link/{provider}")]
        public async Task<IActionResult> LinkExternalLogin(string provider)
        {
            if (User?.Identity?.IsAuthenticated != true)
            {
                return Unauthorized();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User identifier not found");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User not found");
            }

            // Get the external login info
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                // We need to redirect the user to the external provider
                var redirectUrl = Url.Action(nameof(LinkCallback), "ExternalAuthentication");
                var properties = _signInManager.ConfigureExternalAuthenticationProperties(
                    provider, redirectUrl, userId);
                
                return Challenge(properties, provider);
            }

            // The callback will have provided the login info - now link it
            var result = await _userManager.AddLoginAsync(user, info);
            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

            return Ok(new { Message = $"Successfully linked {provider} login" });
        }

        [HttpGet("link-callback")]
        public async Task<IActionResult> LinkCallback()
        {
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return BadRequest("Error getting external login information");
            }

            // Extract user ID from the authentication properties
            var userId = info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return BadRequest("User ID not found in external login info");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User not found");
            }

            var result = await _userManager.AddLoginAsync(user, info);
            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

            return Ok(new { Message = $"Successfully linked {info.LoginProvider} login to your account" });
        }

        [Authorize]
        [HttpDelete("unlink/{provider}")]
        public async Task<IActionResult> RemoveExternalLogin(string provider)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User identifier not found");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User not found");
            }

            var logins = await _userManager.GetLoginsAsync(user);
            var loginToRemove = logins.FirstOrDefault(l => l.LoginProvider == provider);
            if (loginToRemove == null)
            {
                return BadRequest($"No {provider} login found for this account");
            }

            var result = await _userManager.RemoveLoginAsync(user, 
                loginToRemove.LoginProvider, loginToRemove.ProviderKey);
            
            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

            return Ok(new { Message = $"Successfully removed {provider} login from your account" });
        }

       [Authorize]
    [HttpGet("logins")]
    public async Task<IActionResult> GetUserLogins()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("GetUserLogins called for user ID: {UserId}", userId);

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("User identifier not found in GetUserLogins");
            return Unauthorized("User identifier not found");
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("User not found in GetUserLogins for ID: {UserId}", userId);
            return NotFound("User not found");
        }

        var logins = await _userManager.GetLoginsAsync(user);
        _logger.LogInformation("Found {Count} logins for user {UserId}", logins.Count, userId);


        return Ok(new { 
        Logins = logins.Select(l => new {
            Provider = l.LoginProvider,
            ProviderDisplayName = l.ProviderDisplayName
        }).ToList() 
        });
    }
}
}