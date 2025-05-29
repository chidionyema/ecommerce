#nullable enable
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using ecommerce.Db;
using ecommerce.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace ecommerce.Services
{
    public class AuthService
    {
        private readonly UserManager<User> _userManager;
        private readonly IConfiguration _config;
        private readonly ILogger<AuthService> _logger;
        private readonly ecommerceContext _ecommerceContext;
        private readonly SymmetricSecurityKey _securityKey;
        private readonly IWebHostEnvironment _environment;

        public AuthService(
            IWebHostEnvironment environment,
            UserManager<User> userManager,
            IConfiguration config,
            ILogger<AuthService> logger,
            ecommerceContext ecommerceContext)
        {   
            _environment = environment;
            _userManager = userManager;
            _config = config;
            _logger = logger;
            _ecommerceContext = ecommerceContext;
            
            _logger.LogDebug("AuthService: Initializing...");

            var jwtKey = config["Jwt:Key"];
            if (string.IsNullOrEmpty(jwtKey))
            {
                _logger.LogCritical("[CRITICAL] AuthService: JWT Key (Jwt:Key) is not configured in appsettings.");
                throw new InvalidOperationException("JWT Key is not configured. Please check Jwt:Key in configuration.");
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(jwtKey);
                _logger.LogDebug("AuthService: JWT Key successfully decoded from Base64.");
            }
            catch (FormatException ex)
            {
                _logger.LogCritical(ex, "[CRITICAL] AuthService: JWT Key (Jwt:Key) is not a valid Base64 string.");
                throw new InvalidOperationException("JWT Key is not a valid Base64 string.", ex);
            }

            // HS256 (HMAC SHA256) requires a key of at least 256 bits (32 bytes).
            if (keyBytes.Length < 32) 
            {
                _logger.LogCritical("[CRITICAL] AuthService: JWT Key is too weak. Expected at least 32 bytes (256 bits) after Base64 decoding for HS256, but got {KeyByteLength} bytes.", keyBytes.Length);
                throw new InvalidOperationException($"JWT Key is too weak. Expected at least 32 bytes after Base64 decoding, but got {keyBytes.Length} bytes.");
            }
            _securityKey = new SymmetricSecurityKey(keyBytes);
            _logger.LogInformation("AuthService initialized successfully. JWT Issuer: {Issuer}, Audience: {Audience}. Key configured and meets length requirements.", _config["Jwt:Issuer"], _config["Jwt:Audience"]);
        }

        public async Task<JwtSecurityToken> GenerateToken(User user, DateTime expiration)
        {
            _logger.LogDebug("GenerateToken: Attempting to generate token for User ID: {UserId}, Username: {Username}", user.Id, user.UserName);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id),
                new(ClaimTypes.NameIdentifier, user.Id), // Often used interchangeably with Sub
                new(ClaimTypes.Name, user.UserName!), // Ensure UserName is not null
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // Use Jti instead of Token
                new(JwtRegisteredClaimNames.Email, user.Email!) // Ensure Email is not null
            };
            _logger.LogDebug("GenerateToken: Base claims created for User ID: {UserId}. Count: {Count}", user.Id, claims.Count);

            var roles = await _userManager.GetRolesAsync(user);
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
            _logger.LogDebug("GenerateToken: Added {RoleCount} roles for User ID: {UserId}. Roles: [{Roles}]", roles.Count, user.Id, string.Join(", ", roles));

            var userClaims = await _userManager.GetClaimsAsync(user);
            claims.AddRange(userClaims);
            _logger.LogDebug("GenerateToken: Added {UserClaimCount} custom user claims for User ID: {UserId}.", userClaims.Count, user.Id);

            _logger.LogInformation("Generating token for User ID: {UserId}, Username: {Username}, Expires: {Expiration}. Total claims: {TotalClaimsCount}", 
                user.Id, user.UserName, expiration, claims.Count);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: expiration,
                signingCredentials: new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256)
            );
            _logger.LogInformation("GenerateToken: Token generated successfully for User ID: {UserId}. Token: {Token}, Issuer: {Issuer}, Audience: {Audience}, ValidTo: {ValidTo}", user.Id, token.Id, token.Issuer, token.Audiences?.FirstOrDefault(), token.ValidTo);
            return token;
        }

        public async Task<RefreshToken> GenerateRefreshToken(string userId)
        {
            _logger.LogDebug("GenerateRefreshToken: Attempting to generate refresh token for User ID: {UserId}", userId);
            var newTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)); // Secure random token
            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = newTokenValue, // Store the securely generated token value
                Expires = DateTime.UtcNow.AddDays(_config.GetValue<int>("Jwt:RefreshTokenExpiryDays", 7)), // Default to 7 days if not configured
                CreatedAt = DateTime.UtcNow
            };

            _logger.LogInformation("GenerateRefreshToken: New RefreshToken generated for User ID: {UserId}. Token ID: {RefreshTokenId}, Expires: {ExpiryDate}", userId, refreshToken.Id, refreshToken.Expires);
            
            _ecommerceContext.RefreshTokens.Add(refreshToken);
            await _ecommerceContext.SaveChangesAsync();
            _logger.LogInformation("GenerateRefreshToken: New RefreshToken ID: {RefreshTokenId} saved to DB for User ID: {UserId}. Changes saved successfully.", refreshToken.Id, userId);
            return refreshToken;
        }

        public async Task RevokeToken(string tokenValue, string userId, DateTime expiryDate)
        {
            _logger.LogDebug("RevokeToken: Attempting to revoke Access Token. token: '{token}', UserID: '{UserId}', Original Expiry: {ExpiryDate}", tokenValue, userId, expiryDate);
            if (string.IsNullOrEmpty(tokenValue) || string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("RevokeToken: Aborted. Attempted to revoke token with missing token or UserId. token: '{token}', UserId: '{UserId}'", tokenValue, userId);
                return;
            }
            
            // Check if already revoked
            _logger.LogDebug("RevokeToken: Checking if token: {token} is already revoked before adding.", tokenValue);
            if (await IsTokenRevoked(tokenValue)) // This will use the instrumented IsTokenRevoked
            {
                _logger.LogInformation("RevokeToken: Token with token: {token} for User ID: {UserId} is ALREADY marked as revoked. No action taken.", tokenValue, userId);
                return;
            }

            _logger.LogInformation("RevokeToken: Proceeding to add token: {token} for UserID: {UserId} to revoked list. Original Expiry: {ExpiryDate}", tokenValue, userId, expiryDate);
            _ecommerceContext.RevokedTokens.Add(new RevokedToken
            {
                Id = Guid.NewGuid(),
                Token = tokenValue,
                UserId = userId,
                ExpiresAt = expiryDate, // Use ExpiresAt instead of ExpiryDate
                RevokedAt = DateTime.UtcNow, // Use RevokedAt instead of CreatedAt
                Reason = "Manual revocation"
            });
            await _ecommerceContext.SaveChangesAsync();
            _logger.LogInformation("RevokeToken: Token token: {token} for User ID: {UserId} successfully added to revoked list in DB. SaveChanges completed.", tokenValue, userId);
        }

        public async Task RevokeRefreshTokensForUserAsync(string userId)
        {
            _logger.LogDebug("RevokeRefreshTokensForUserAsync: Called for User ID: {UserId}", userId);
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("RevokeRefreshTokensForUserAsync: Aborted. Called with null or empty userId.");
                return; 
            }

            _logger.LogInformation("Attempting to revoke all active refresh tokens for User ID: {UserId}", userId);

            var userRefreshTokens = await _ecommerceContext.RefreshTokens
                .Where(rt => rt.UserId == userId) // Only active ones if you have an IsRevoked flag on RefreshToken itself
                .ToListAsync();

            if (userRefreshTokens.Any())
            {
                _logger.LogDebug("RevokeRefreshTokensForUserAsync: Found {Count} refresh token(s) for User ID: {UserId} to revoke.", userRefreshTokens.Count, userId);
                _ecommerceContext.RefreshTokens.RemoveRange(userRefreshTokens);
                await _ecommerceContext.SaveChangesAsync();
                _logger.LogInformation("Successfully revoked {Count} refresh token(s) for User ID: {UserId}. SaveChanges completed.", userRefreshTokens.Count, userId);
            }
            else
            {
                _logger.LogInformation("No active refresh tokens found for User ID: {UserId} to revoke.", userId);
            }
        }

        // CRITICAL FOR REVOCATION CHECKS
        public async Task<bool> IsTokenRevoked(string tokenValue)
        {
            _logger.LogDebug("IsTokenRevoked: Checking revocation status for token: '{token}'", tokenValue);
            if (string.IsNullOrEmpty(tokenValue)) {
                _logger.LogWarning("IsTokenRevoked: token is null or empty. Returning false (cannot be revoked if no token).");
                return false; // Cannot be revoked if it has no token
            }
            // Query the database for this token in the revoked tokens table
            bool isRevoked = await _ecommerceContext.RevokedTokens.AnyAsync(rt => rt.Token == tokenValue);
            _logger.LogInformation("IsTokenRevoked: Database check for token: '{token}'. Is Revoked: {IsRevoked}", tokenValue, isRevoked);
            return isRevoked;
        }

        public void SetSecureCookie(HttpContext context, JwtSecurityToken token)
        {
            _logger.LogDebug("SetSecureCookie: Attempting to set 'jwt' cookie.");
            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,  // Client-side script cannot access the cookie
                Secure = _environment.IsProduction(),    // Transmit cookie only over HTTPS
                SameSite = SameSiteMode.Strict, // Mitigates CSRF attacks
                Expires = token.ValidTo, // Cookie lifetime matches token lifetime
                Path = "/",       // Cookie available to all paths
                IsEssential = true // For GDPR compliance if needed, marks cookie as essential
            };
            context.Response.Cookies.Append("jwt", tokenString, cookieOptions);
            _logger.LogInformation("SetSecureCookie: JWT cookie 'jwt' set. HttpOnly: {HttpOnly}, Secure: {Secure}, SameSite: {SameSite}, Expires: {Expiration}, Path: {Path}", 
                cookieOptions.HttpOnly, cookieOptions.Secure, cookieOptions.SameSite, cookieOptions.Expires, cookieOptions.Path);
        }

        public void DeleteAuthCookie(HttpContext context)
        {
            _logger.LogDebug("DeleteAuthCookie: Attempting to delete 'jwt' cookie.");
            // To delete a cookie, you append it again with an expiry date in the past.
            // Ensure the path and domain match the original cookie if they were set.
            context.Response.Cookies.Delete("jwt", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/" 
                // Expires = DateTime.UtcNow.AddDays(-1) // Not strictly needed with Delete but can be explicit
            });
            _logger.LogInformation("DeleteAuthCookie: 'jwt' cookie deletion requested.");
        }

        // This method prepares parameters for token validation. Logs here show configuration.
        public TokenValidationParameters GetTokenValidationParameters(bool validateLifetime = true)
        {
            _logger.LogDebug("GetTokenValidationParameters: Creating TokenValidationParameters. ValidateLifetime: {ValidateLifetime}", validateLifetime);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _securityKey,
                ValidateIssuer = true,
                ValidIssuer = _config["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _config["Jwt:Audience"],
                ValidateLifetime = validateLifetime, // Whether to check nbf, exp, and custom lifetime
                ClockSkew = TimeSpan.Zero, // No clock skew tolerance
                LifetimeValidator = validateLifetime ? CustomLifetimeValidator : null // Use custom validator if validating lifetime
            };
            _logger.LogInformation("GetTokenValidationParameters: Parameters created. Issuer: {Issuer}, Audience: {Audience}, ValidateLifetime: {ParamValidateLifetime}, CustomValidatorSet: {IsCustomValidatorSet}",
                parameters.ValidIssuer, parameters.ValidAudience, parameters.ValidateLifetime, parameters.LifetimeValidator != null);
            return parameters;
        }

        // CRITICAL FOR TOKEN VALIDATION LOGIC (INCLUDING REVOCATION)
        private bool CustomLifetimeValidator(DateTime? notBefore, DateTime? expires, SecurityToken securityToken, TokenValidationParameters validationParameters)
        {
            var utcNow = DateTime.UtcNow;
            _logger.LogDebug("CustomLifetimeValidator: Entered. Validating token. NotBefore: {NotBefore}, Expires: {Expires}, Current UTC: {UtcNow}. SecurityToken Type: {TokenType}", 
                notBefore, expires, utcNow, securityToken?.GetType().Name);

            if (expires.HasValue && expires.Value < utcNow)
            {
                _logger.LogWarning("CustomLifetimeValidator: Validation FAILED - Token EXPIRED. Expires: {Expires}, Current UTC: {UtcNow}", expires.Value, utcNow);
                return false; // Path 1: Expired
            }
            if (notBefore.HasValue && notBefore.Value > utcNow)
            {
                _logger.LogWarning("CustomLifetimeValidator: Validation FAILED - Token NOT YET VALID (NBF). NotBefore: {NotBefore}, Current UTC: {UtcNow}", notBefore.Value, utcNow);
                return false; // Path 2: Not yet valid
            }
            _logger.LogDebug("CustomLifetimeValidator: Basic lifetime checks (NBF/EXP) passed. Expires: {ExpiresValue}, NotBefore: {NotBeforeValue}", expires, notBefore);

            if (securityToken is JwtSecurityToken jwtToken)
            {
                var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
                _logger.LogDebug("CustomLifetimeValidator: Token is JwtSecurityToken. Extracted JTI: '{jti}'", jti);

                if (string.IsNullOrEmpty(jti))
                {
                    _logger.LogWarning("CustomLifetimeValidator: Validation FAILED - JTI (JWT ID) is MISSING from the token. Cannot check for revocation.");
                    return false; // Path 3: JTI missing, cannot check revocation
                }
                
                // This call will trigger logs from the IsTokenRevoked method.
                _logger.LogDebug("CustomLifetimeValidator: Calling IsTokenRevoked for JTI: '{jti}'...", jti);
                // .GetAwaiter().GetResult() makes this a synchronous call to an async method.
                // Ensure IsTokenRevoked and EF Core calls are robust.
                bool isTokenActuallyRevoked = IsTokenRevoked(jti).GetAwaiter().GetResult(); 
                // Logs from IsTokenRevoked (e.g., "IsTokenRevoked: Database check for token: '{token}'. Is Revoked: {IsRevoked}") will appear before the next log.
                
                if (isTokenActuallyRevoked)
                {
                    _logger.LogWarning("CustomLifetimeValidator: Validation FAILED - Token JTI ('{jti}') IS REVOKED based on IsTokenRevoked check.", jti);
                    return false; // Path 4: Token is actively revoked
                }
                _logger.LogInformation("CustomLifetimeValidator: Validation PASSED for JTI ('{jti}'). Token is not expired, within NBF, and not revoked.", jti);
                return true; // Path 5: All checks passed (lifetime + not revoked)
            }
            
            _logger.LogWarning("CustomLifetimeValidator: Validation FAILED - SecurityToken is NOT a JwtSecurityToken. Actual Type: {TokenType}. Cannot extract JTI for revocation check.", securityToken?.GetType().Name);
            return false; // Path 6: Not a JWT, cannot perform token-based revocation
        }

        // CRITICAL FOR OVERALL TOKEN VALIDATION
        public ClaimsPrincipal? ValidateToken(string tokenString, bool validateLifetime = true)
        {
            var SENSITIVE_TOKEN_LOG_LENGTH = 30; // How many characters of token to log for identification
            var tokenSnippet = tokenString?.Substring(0, Math.Min(tokenString?.Length ?? 0, SENSITIVE_TOKEN_LOG_LENGTH)) + (tokenString?.Length > SENSITIVE_TOKEN_LOG_LENGTH ? "..." : "");
            _logger.LogDebug("ValidateToken: Attempting to validate token. ValidateLifetime: {ValidateLifetime}. Token (snippet): '{TokenSnippet}'", validateLifetime, tokenSnippet);

            if (string.IsNullOrEmpty(tokenString))
            {
                _logger.LogWarning("ValidateToken: Validation FAILED - Input token is null or empty.");
                return null;
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                var validationParameters = GetTokenValidationParameters(validateLifetime); // This also logs
                _logger.LogDebug("ValidateToken: Using TokenValidationParameters - IssuerSigningKey set: {IsKeySet}, ValidIssuer: {ValidIssuer}, ValidAudience: {ValidAudience}, ValidateLifetime: {ParamValidateLifetime}, CustomLifetimeValidator set: {IsCustomValidatorSet}",
                    validationParameters.IssuerSigningKey != null, validationParameters.ValidIssuer, validationParameters.ValidAudience, validationParameters.ValidateLifetime, validationParameters.LifetimeValidator != null);

                _logger.LogDebug("ValidateToken: Calling JwtSecurityTokenHandler.ValidateToken method...");
                // This call will invoke CustomLifetimeValidator if validationParameters.LifetimeValidator is set and ValidateLifetime is true.
                var principal = tokenHandler.ValidateToken(tokenString, validationParameters, out SecurityToken validatedToken);
                
                var jwtValidatedToken = validatedToken as JwtSecurityToken;
                var jti = jwtValidatedToken?.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value ?? "N/A";
                var identityName = principal?.Identity?.Name ?? "N/A (anonymous)";
                var claimsCount = principal?.Claims?.Count() ?? 0;

                _logger.LogInformation("ValidateToken: Token validation SUCCEEDED. JTI: {jti}, IdentityName: '{IdentityName}', ClaimsCount: {ClaimsCount}. ValidatedToken Type: {ValidatedTokenType}", 
                    jti, identityName, claimsCount, validatedToken?.GetType().Name);
                
                if (_logger.IsEnabled(LogLevel.Debug)) // Only log all claims if Debug is enabled due to verbosity
                {
                    var claimsString = string.Join(" | ", principal?.Claims.Select(c => $"{c.Type}: '{c.Value}'") ?? Enumerable.Empty<string>());
                    _logger.LogDebug("ValidateToken: Successfully validated claims: [{ClaimsString}]", claimsString);
                }
                return principal;
            }
            catch (SecurityTokenExpiredException ex)
            {
                _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenExpiredException. Token is expired. JTI (if parsable from expired token): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (SecurityTokenInvalidSignatureException ex)
            {
                _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenInvalidSignatureException. Token signature is invalid. JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (SecurityTokenNoExpirationException ex)
            {
                 _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenNoExpirationException. Token has no expiration claim (exp). JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (SecurityTokenNotYetValidException ex)
            {
                 _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenNotYetValidException. Token is not yet valid (nbf). JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (SecurityTokenReplayAddFailedException ex) 
            {
                _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenReplayAddFailedException. Token replay detected. JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (SecurityTokenValidationException ex) 
            {
                // This is often a wrapper if CustomLifetimeValidator returns false.
                // The CustomLifetimeValidator should have logged the specific reason (e.g., revoked, expired).
                _logger.LogWarning(ex, "ValidateToken: Validation FAILED - SecurityTokenValidationException. This often indicates failure in custom validation (like lifetime or revocation). Check previous logs from CustomLifetimeValidator. JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
            catch (Exception ex) // Catch-all for unexpected errors
            {
                _logger.LogError(ex, "ValidateToken: Validation FAILED - Unexpected critical error during token validation. JTI (if parsable): {jti}", GetJtiFromPossiblyInvalidToken(tokenString, tokenHandler));
                return null;
            }
        }

        // Helper to attempt to get JTI from a token string, even if it's invalid, for logging purposes.
        private string GetJtiFromPossiblyInvalidToken(string tokenString, JwtSecurityTokenHandler handler)
        {
            if (string.IsNullOrEmpty(tokenString) || handler == null || !handler.CanReadToken(tokenString)) return "N/A (unreadable)";
            try
            {
                var jwtToken = handler.ReadJwtToken(tokenString);
                return jwtToken?.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value ?? "N/A (JTI missing)";
            }
            catch { return "N/A (read error)"; }
        }
    }
}