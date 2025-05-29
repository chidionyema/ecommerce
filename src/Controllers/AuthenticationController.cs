using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration; // Not directly used in methods, but common for controllers
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens; // Used by AuthService, not directly here
using System;
using System.Collections.Generic; // For DTOs if they were complex
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using ecommerce.Models; // For User model
using ecommerce.Dto;   // For DTOs
using ecommerce.Db;    // For ecommerceContext and RefreshToken entity
using ecommerce.Services; // For AuthService
 // Potentially for ecommerceContext if not from Db
using Microsoft.EntityFrameworkCore; // For FirstOrDefaultAsync
using Microsoft.AspNetCore.Http; // For HttpContext
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations; // For DTO validation attributes

namespace ecommerce.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthenticationController : ControllerBase
    {
        private readonly UserManager<User> _userManager;
        private readonly ILogger<AuthenticationController> _logger;
        private readonly ecommerceContext _ecommerceContext;
        private readonly AuthService _authService;

        public AuthenticationController(
            UserManager<User> userManager,
            ILogger<AuthenticationController> logger,
            ecommerceContext ecommerceContext, // Assuming this is the correct DbContext type name
            AuthService authService)
        {
            _userManager = userManager;
            _logger = logger; 
            _ecommerceContext = ecommerceContext;
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] UserRegistrationDto registrationDto)
        {
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid registration attempt => Model errors: {Errors}",
                    string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
                return BadRequest(ModelState);
            }

            _logger.LogInformation("Attempting to register user: {Username}", registrationDto.Username);
            var user = new User
            {
                UserName = registrationDto.Username,
                Email = registrationDto.Email
                // Other properties like EmailConfirmed, PhoneNumber etc., might be set here or by default
            };

            var result = await _userManager.CreateAsync(user, registrationDto.Password);
            if (!result.Succeeded)
            {
                // Log individual errors
                foreach (var err in result.Errors)
                {
                    _logger.LogWarning("Registration error for {Username}: {ErrorCode} - {ErrorDescription}", 
                        registrationDto.Username, err.Code, err.Description);
                }
                return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
            }

            _logger.LogInformation("User registration succeeded for user: {Username}, Id: {UserId}",
                user.UserName, user.Id);

            // Add to a default role, e.g., "User" or a specific one like "ContentUploader"
            // Ensure these roles exist (can be seeded at startup)
            var roleResult = await _userManager.AddToRoleAsync(user, "ContentUploader"); // Example role
            if (!roleResult.Succeeded)
            {
                // Log error but might not fail the entire registration depending on policy
                 _logger.LogError("Failed to add user {UserId} to role 'ContentUploader'. Errors: {Errors}", 
                    user.Id, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                return BadRequest(new { message = "User registered, but role assignment failed.", errors = roleResult.Errors.Select(e => e.Description) });
            }

            var claimResult = await _userManager.AddClaimAsync(
                user, new Claim("permission", "upload_content") // Example claim
            );
            if (!claimResult.Succeeded)
            {
                 _logger.LogError("Failed to add claim 'permission:upload_content' for user {UserId}. Errors: {Errors}", 
                    user.Id, string.Join(", ", claimResult.Errors.Select(e => e.Description)));
                return BadRequest(new { message = "User registered, but claim assignment failed.", errors = claimResult.Errors.Select(e => e.Description) });
            }   

            var token = await _authService.GenerateToken(user, DateTime.UtcNow.AddMinutes(15)); // Or a configurable duration
            _authService.SetSecureCookie(HttpContext, token); // Set token in cookie

            return Ok(new
            {
                message = "Registration successful", // Added a message
                token = new JwtSecurityTokenHandler().WriteToken(token),
                userId = user.Id,
                username = user.UserName, // Return username
                email = user.Email,       // Return email
                expires = token.ValidTo
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] UserLoginDto loginDto)
        {
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Invalid login attempt => Model errors: {Errors}",
                    string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
                return BadRequest(ModelState);
            }

            _logger.LogInformation("Attempting to login user: {Username}", loginDto.Username);
            var user = await _userManager.FindByNameAsync(loginDto.Username);
            if (user == null)
            {
                _logger.LogWarning("User not found during login attempt: {Username}", loginDto.Username);
                return Unauthorized(new { message = "Invalid username or password." }); // Generic message
            }

            // Consider account lockout policies here if using Identity's SignInManager
            var passwordValid = await _userManager.CheckPasswordAsync(user, loginDto.Password);
            if (!passwordValid)
            {
                _logger.LogWarning("Invalid password for user: {Username}", loginDto.Username);
                // TODO: Increment failed access count for user if lockout is enabled
                return Unauthorized(new { message = "Invalid username or password." }); // Generic message
            }

            _logger.LogInformation("Login successful for user: {Username}, Id: {UserId}", user.UserName, user.Id);
            // TODO: Reset failed access count on successful login if lockout is enabled

            var token = await _authService.GenerateToken(user, DateTime.UtcNow.AddMinutes(15)); // Or a configurable duration
            var refreshTokenEntity = await _authService.GenerateRefreshToken(user.Id); // Assuming user.Id is string
            _authService.SetSecureCookie(HttpContext, token); // Set access token in cookie

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                refreshToken = refreshTokenEntity.Token, // Send new refresh token to client
                user = new
                {
                    id = user.Id,
                    userName = user.UserName,
                    email = user.Email
                    // Add roles or other relevant user info if needed
                },
                expires = token.ValidTo
            });
        }

        [HttpPost("logout")]
        [Authorize] // Ensure user is authenticated to logout
        public async Task<IActionResult> Logout()
        {   _logger.LogInformation(
            "[Logout] Method entered. User Authenticated: {IsAuthenticated}. AuthenticationType: {AuthType}",
            User.Identity?.IsAuthenticated,
            User.Identity?.AuthenticationType
        );
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _logger.LogInformation("Logout called for User ID: {UserId}", userId ?? "Unknown (claim not found)");

            var jti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var expiryClaim = User.FindFirstValue(JwtRegisteredClaimNames.Exp);
            
            if (!string.IsNullOrEmpty(jti) && !string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(expiryClaim) && long.TryParse(expiryClaim, out long expiryUnixTime))
            {
                var expiryDate = DateTimeOffset.FromUnixTimeSeconds(expiryUnixTime).UtcDateTime;
                await _authService.RevokeToken(jti, userId, expiryDate);
                _logger.LogInformation("Access token (JTI: {Jti}) for User ID: {UserId} marked as revoked.", jti, userId);
            }
            else
            {
                _logger.LogWarning("Could not revoke token for User ID: {UserId} due to missing JTI, UserID, or Expiry claim.", userId ?? "Unknown");
            }

            // Also revoke any active refresh tokens associated with the user session if applicable
            if (!string.IsNullOrEmpty(userId))
            {
                await _authService.RevokeRefreshTokensForUserAsync(userId); // Example method, implement in AuthService
            }
            
            _authService.DeleteAuthCookie(HttpContext); // Delete the access token cookie
            // Consider deleting refresh token cookie if it's separate and managed by client
            
            return Ok(new { message = "Logged out successfully" });
        }

        [Authorize]
        [HttpGet("verify-token")]
        public async Task<IActionResult> VerifyToken()
        {  _logger.LogInformation(
            "[VerifyToken] Method entered. User Authenticated: {IsAuthenticated}. AuthenticationType: {AuthType}",
            User.Identity?.IsAuthenticated,
            User.Identity?.AuthenticationType
        );
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("[VerifyToken] Authorized user but no userId claim (NameIdentifier or Sub) found. Claims: {Claims}", 
                    string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}")));
                
                // Attempt fallback if Identity.Name is available and reliable
                var identityName = User.Identity?.Name;
                if (!string.IsNullOrEmpty(identityName))
                {
                    var userByName = await _userManager.FindByNameAsync(identityName);
                    if (userByName != null)
                    {
                        userId = userByName.Id;
                        _logger.LogInformation("[VerifyToken] User ID resolved via Identity.Name to {UserId}", userId);
                    }
                }
                
                if (string.IsNullOrEmpty(userId))
                {
                    // This state is unusual: authenticated but no identifiable user ID.
                    return Ok(new { 
                        message = "Token is valid, but user identifier could not be resolved from claims.",
                        isAuthenticated = true, // User.Identity.IsAuthenticated is true due to [Authorize]
                        identityName = User.Identity?.Name // May or may not be present
                    });
                }
            }

            // If userId was found (either from primary claims or fallback)
            var user = await _userManager.FindByIdAsync(userId); // userId is now string, not string? due to above logic
            if (user == null)
            {
                _logger.LogWarning("[VerifyToken] User ID '{UserId}' from token not found in database. Token might be for a deleted user.", userId);
                // Token is technically valid, but user doesn't exist.
                // Depending on policy, could return an error or just indicate user not found.
                return Conflict(new { 
                    message = "Token is valid, but the associated user account no longer exists.",
                    userId = userId,
                    isAuthenticated = true
                });
            }

            _logger.LogInformation("[VerifyToken] Token verified for User ID: {UserId}, Username: {Username}", user.Id, user.UserName);
            return Ok(new { userId = user.Id, userName = user.UserName, isAuthenticated = true });
        }

        [HttpPost("refresh-token")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.AccessToken) || string.IsNullOrEmpty(request.RefreshToken))
            {
                return BadRequest(new { message = "Access token and refresh token are required." });
            }

            _logger.LogInformation("RefreshToken called with AccessToken (length: {AccessLen}), RefreshToken (length: {RefreshLen})",
                request.AccessToken.Length, request.RefreshToken.Length);

            try
            {
                var principal = _authService.ValidateToken(request.AccessToken, validateLifetime: false); // Validate structure, not expiry
                if (principal == null)
                {
                    _logger.LogWarning("RefreshToken: Invalid access token structure or signature.");
                    return Unauthorized(new { message = "Invalid access token."});
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? 
                             principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
                
                // FIXED CS8604: Add null check for userId before using it with FindByIdAsync
                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("RefreshToken: User ID claim (NameIdentifier or Sub) not found in the expired access token.");
                    return Unauthorized(new { message = "Invalid token: User identifier missing." });
                }
                             
                _logger.LogInformation("Attempting refresh for potential userId: {UserId} from expired token", userId);

                var user = await _userManager.FindByIdAsync(userId); // userId is now guaranteed non-null
                if (user == null)
                {
                    _logger.LogWarning("RefreshToken: User ({UserId}) found in token claims does not exist in database.", userId);
                    return Unauthorized(new { message = "User associated with token not found." });
                }

                // Validate the provided refresh token against the database
                var storedToken = await _ecommerceContext.RefreshTokens
                    .FirstOrDefaultAsync(rt => rt.Token == request.RefreshToken && rt.UserId == userId);

                if (storedToken == null)
                {
                    _logger.LogWarning("RefreshToken: Refresh token not found in DB or doesn't match user {UserId}.", userId);
                    return Unauthorized(new { message = "Invalid refresh token." });
                }

                if (storedToken.Expires < DateTime.UtcNow)
                {
                    _logger.LogWarning("RefreshToken: Stored refresh token for user {UserId} expired on {ExpiredOn}.",
                        userId, storedToken.Expires);
                    _ecommerceContext.RefreshTokens.Remove(storedToken); // Clean up expired token
                    await _ecommerceContext.SaveChangesAsync();
                    return Unauthorized(new { message = "Refresh token expired." });
                }

                // Refresh token is valid, remove old one, generate new ones
                _ecommerceContext.RefreshTokens.Remove(storedToken);
                // SaveChangesAsync will be called by GenerateRefreshToken or explicitly after

                var newAccessToken = await _authService.GenerateToken(user, DateTime.UtcNow.AddMinutes(15)); // Or configurable duration
                var newRefreshToken = await _authService.GenerateRefreshToken(user.Id); // This should save changes

                _logger.LogInformation("Generated new access and refresh tokens for user {UserId}. New RefreshToken expires {Expires}",
                    user.Id, newRefreshToken.Expires);

                return Ok(new
                {
                    accessToken = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                    refreshToken = newRefreshToken.Token,
                    expires = newAccessToken.ValidTo
                });
            }
            catch (SecurityTokenException ex) // Catch specific exceptions from ValidateToken if needed
            {
                _logger.LogWarning(ex, "RefreshToken: SecurityTokenException during token processing.");
                return Unauthorized(new { message = $"Invalid token: {ex.Message}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh token endpoint failed unexpectedly.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An internal error occurred." });
            }
        }

        [HttpGet("debug-auth")]
        [Authorize] // Ensures User.Identity and User.Identity.IsAuthenticated are usually set if token is valid
        public IActionResult DebugAuth()
        {
            // FIXED CS8602: Corrected the if condition and explicitly used null-forgiving on User.Identity
            // If [Authorize] is present, User.Identity should be non-null and IsAuthenticated should be true.
            // The `?? false` handles the case where User.Identity itself might be null, making the whole expression safe.
            if (!(User.Identity?.IsAuthenticated ?? false))
            {
                // This block should ideally not be reached if [Authorize] works as expected.
                _logger.LogWarning("[DebugAuth] User.Identity.IsAuthenticated is false or User.Identity is null, despite [Authorize] attribute.");
                return Unauthorized(new { message = "User not authenticated (or identity information missing).", 
                                        claims = User.Claims.Select(c => new { c.Type, c.Value }) });
            }
            
            // At this point, User.Identity is effectively non-null due to the check above and [Authorize]
            var claims = User.Claims.ToDictionary(c => c.Type, c => c.Value);
            var authType = User.Identity!.AuthenticationType; // Using null-forgiving `!` as logic dictates User.Identity is not null here.
            
            return Ok(new { 
                message = "Authentication successful", 
                authType = authType, // Explicitly using the variable
                userId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                userName = User.FindFirstValue(ClaimTypes.Name),
                role = User.FindFirstValue(ClaimTypes.Role),
                claims
            });
        }
    }

    public class UserRegistrationDto
    {
        [Required(ErrorMessage = "Username is required.")]
        [MinLength(3, ErrorMessage = "Username must be at least 3 characters long.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Invalid email address format.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
        // Add other password complexity attributes if needed, e.g., [DataType(DataType.Password)]
        // [RegularExpression("^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)(?=.*[^\\da-zA-Z]).{8,}$", ErrorMessage = "Password must meet complexity requirements.")]
        public string Password { get; set; } = string.Empty;
    }

    public class UserLoginDto
    {
        [Required(ErrorMessage = "Username is required.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        public string Password { get; set; } = string.Empty;
    }

    public class RefreshTokenRequest
    {
        [Required(ErrorMessage = "Access token is required.")]
        public string AccessToken { get; set; } = string.Empty;

        [Required(ErrorMessage = "Refresh token is required.")]
        public string RefreshToken { get; set; } = string.Empty;
    }
}