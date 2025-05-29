// File: ecommerce.Controllers/TestAuthController.cs (Create this file in your project)

// This preprocessor directive ensures this controller is only compiled
// in DEBUG builds or if a "TESTING" compilation symbol is defined (e.g., in your test project's build configuration).
#if DEBUG || TESTING

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration; // Required for IConfiguration
using Microsoft.Extensions.Logging;     // Required for ILogger
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
namespace ecommerce.Controllers // Use your project's appropriate namespace
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestAuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TestAuthController> _logger;

        public TestAuthController(IConfiguration configuration, ILogger<TestAuthController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Generates a JWT for testing purposes.
        /// IMPORTANT: This endpoint should ONLY be available in test/dev environments.
        /// </summary>
        /// <param name="userId">The user ID to include in the token's 'sub' claim.</param>
        /// <param name="userName">The username to include in the token's 'name' claim.</param>
        /// <param name="roles">Optional comma-separated list of roles.</param>
        /// <returns>A JWT token and user details.</returns>
        [HttpGet("generate-test-token")]
        public IActionResult GenerateTestToken([FromQuery] string userId, [FromQuery] string userName, [FromQuery] string? roles = null)
        {
            _logger.LogInformation("TestAuthController: generate-test-token called for UserId: {UserId}, UserName: {UserName}", userId, userName);

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userName))
            {
                _logger.LogWarning("TestAuthController: UserId or UserName is missing.");
                return BadRequest(new { message = "userId and userName query parameters are required." });
            }

            // Retrieve JWT settings from your application's configuration
            // These key names ("JwtSettings:Key", "JwtSettings:Issuer", etc.) must match your actual configuration structure.
            var jwtKey = _configuration["JwtSettings:Key"]; // e.g., from appsettings.Development.json or user secrets
            var jwtIssuer = _configuration["JwtSettings:Issuer"];
            var jwtAudience = _configuration["JwtSettings:Audience"];
            var expiresInMinutes = _configuration.GetValue<int>("JwtSettings:ExpiresInMinutes", 15); // Default to 15 minutes for test tokens

            if (string.IsNullOrEmpty(jwtKey) || string.IsNullOrEmpty(jwtIssuer) || string.IsNullOrEmpty(jwtAudience))
            {
                _logger.LogCritical("TestAuthController: JWT settings (Key, Issuer, or Audience) are not configured in appsettings or secrets. Cannot generate test token.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Server-side JWT settings are missing or incomplete for test token generation." });
            }
             if (jwtKey.Length < 32) // Example check for a common HmacSha256 key length requirement
            {
                _logger.LogCritical("TestAuthController: JWT Key is too short. Ensure it's a strong, sufficiently long key. Key length: {KeyLength}", jwtKey.Length);
                 return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Server-side JWT key configuration is insecure for test token generation." });
            }


            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),        // Standard subject claim (user ID)
                new Claim(JwtRegisteredClaimNames.Name, userName),      // Standard name claim
                new Claim(ClaimTypes.NameIdentifier, userId),           // Another common way to store user ID
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) // Unique token identifier
                // Add any other claims your application expects or uses, e.g., email, roles
                // new Claim(JwtRegisteredClaimNames.Email, $"{userName.ToLower()}@example.com"),
            };

            if (!string.IsNullOrEmpty(roles))
            {
                foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
                }
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(expiresInMinutes),
                Issuer = jwtIssuer,
                Audience = jwtAudience,
                SigningCredentials = credentials
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var securityToken = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(securityToken);

            _logger.LogInformation("TestAuthController: Successfully generated token for UserId: {UserId}", userId);

            return Ok(new
            {
                Token = tokenString,
                UserId = userId,
                UserName = userName,
                Expires = tokenDescriptor.Expires.Value // Expiration time of the generated token
            });
        }
    }
}
#endif // End of #if DEBUG || TESTING