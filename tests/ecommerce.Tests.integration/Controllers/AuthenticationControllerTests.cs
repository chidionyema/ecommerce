using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ecommerce.Controllers;
using ecommerce.Db;
using ecommerce.Dto;
using ecommerce.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Xunit;
using Microsoft.Extensions.DependencyInjection;


namespace ecommerce.Tests.Integration
{
    [Collection("Integration Tests")]
    public class AuthenticationControllerTests : IAsyncLifetime
    {
        private readonly IntegrationTestFixture _fixture;

        // (A) The original client that shares the CookieContainer
        private readonly HttpClient _client; 
        
        // (B) A second client with NO cookies, for Bearer-based tests
        private readonly HttpClient _clientNoCookies;

        public AuthenticationControllerTests(IntegrationTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.CreateClientWithCookies();
            // Create a second client without a cookie container:
            _clientNoCookies = fixture.Factory.CreateClient();
        }

        // Runs before each test class execution
        public async Task InitializeAsync()
        {
            await _fixture.ResetDatabaseAsync();
        }

        // Runs after each test class execution
        public Task DisposeAsync() => Task.CompletedTask;

        // ------------------------------------------------------
        // SECTION 1: Original Cookie-based Tests
        // ------------------------------------------------------

        #region Registration Tests (Cookie-based)

        [Fact(DisplayName = "[Cookie] POST /register - Success (Valid Model)")]
        public async Task Register_ValidModel_ReturnsOk()
        {
            // Arrange
            var registrationDto = new UserRegistrationDto
            {
                Username = $"reg_success_{Guid.NewGuid()}",
                Email = "testuser@example.com",
                Password = "Password123!"
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/register", registrationDto);
            var responseBody = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Validate JSON fields
            var json = JObject.Parse(responseBody);
            Assert.True(json.ContainsKey("token"), "Response should contain 'token'");
            Assert.True(json.ContainsKey("userId"), "Response should contain 'userId'");
            Assert.True(json.ContainsKey("expires"), "Response should contain 'expires'");
        }

        [Fact(DisplayName = "[Cookie] POST /register - Failure (Invalid Model)")]
        public async Task Register_InvalidModel_ReturnsBadRequest()
        {
            // Arrange: Missing email, invalid password
            var registrationDto = new UserRegistrationDto
            {
                Username = "invalid_user",
                Email = "not-an-email", 
                Password = "123"  // fails MinLength=8
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/register", registrationDto);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact(DisplayName = "[Cookie] POST /register - Failure (Duplicate Username)")]
        public async Task Register_DuplicateUsername_ReturnsBadRequest()
        {
            // Arrange
            var username = $"dup_user_{Guid.NewGuid()}";
            var firstDto = new UserRegistrationDto
            {
                Username = username,
                Email = $"{username}@example.com",
                Password = "Password123!"
            };
            var secondDto = new UserRegistrationDto
            {
                Username = username, // same username
                Email = $"another_{username}@example.com",
                Password = "AnotherPass123!"
            };

            // Act: First registration is successful
            var firstResponse = await _client.PostAsJsonAsync("api/authentication/register", firstDto);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

            // Second registration with the same username should fail
            var secondResponse = await _client.PostAsJsonAsync("api/authentication/register", secondDto);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
        }

        #endregion

        #region Login Tests (Cookie-based)

        [Fact(DisplayName = "[Cookie] POST /login - Success (Valid Credentials)")]
        public async Task Login_ValidCredentials_ReturnsOk()
        {
            // Arrange: Register the user first
            var username = $"login_success_{Guid.NewGuid()}";
            var password = "Password123!";
            await RegisterTestUserCookie(username, $"{username}@example.com", password);

            var loginDto = new UserLoginDto
            {
                Username = username,
                Password = password
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/login", loginDto);
            var responseBody = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = JObject.Parse(responseBody);
            Assert.True(json.ContainsKey("token"), "Response should contain 'token'.");
            Assert.True(json.ContainsKey("user"), "Response should contain 'user'.");
            Assert.True(json.ContainsKey("expires"), "Response should contain 'expires'.");

            // Additional check on user object
            var userObj = json["user"] as JObject;
            Assert.Equal(username, userObj?["userName"]?.ToString());
        }

        [Fact(DisplayName = "[Cookie] POST /login - Failure (Invalid Credentials)")]
        public async Task Login_InvalidCredentials_ReturnsUnauthorized()
        {
            // Arrange: This user doesn't exist
            var loginDto = new UserLoginDto
            {
                Username = "non_existent",
                Password = "WrongPassword"
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/login", loginDto);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact(DisplayName = "[Cookie] POST /login - Failure (Invalid Model => BadRequest)")]
        public async Task Login_InvalidModel_ReturnsBadRequest()
        {
            // Arrange: Missing password
            var loginDto = new UserLoginDto
            {
                Username = "some_user"
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/login", loginDto);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region Logout Tests (Cookie-based)

   [Fact(DisplayName = "[Cookie] POST /logout - Success (User Session Terminated)")]
public async Task Logout_Success_TerminatesUserSession()
{
    // Arrange: Register & Login to set the jwt cookie
    var username = $"logout_{Guid.NewGuid()}";
    var password = "Password123!";
    await RegisterTestUserCookie(username, $"{username}@example.com", password);
    await LoginTestUserCookie(username, password);

    // Verify we're authenticated before logout
    var preLogoutResponse = await _client.GetAsync("api/authentication/verify-token");
    Assert.Equal(HttpStatusCode.OK, preLogoutResponse.StatusCode);

    // Act: Call /logout
    var response = await _client.PostAsync("api/authentication/logout", null);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    
    // Create a new HTTP client that uses the same cookie container
    // This will simulate a new request with the same cookie container
    var clientWithSameCookies = _fixture.CreateClientWithCookies();
    
    // Try to access a protected endpoint
    var verifyResponse = await clientWithSameCookies.GetAsync("api/authentication/verify-token");
    
    // The key assertion: verify that the server treats us as logged out
    // even though the cookie might technically still be in the container
    Assert.Equal(HttpStatusCode.Unauthorized, verifyResponse.StatusCode);
}

        #endregion

        #region Verify-Token Tests (Cookie-based)

        [Fact(DisplayName = "[Cookie] GET /verify-token - Success (Valid Cookie)")]
        public async Task VerifyToken_ValidCookie_ReturnsOk()
        {
            // Arrange: Register & Login
            var username = $"verify_{Guid.NewGuid()}";
            var password = "Password123!";
            var userId = await RegisterTestUserCookie(username, $"{username}@example.com", password);
            await LoginTestUserCookie(username, password);

            // Act
            var response = await _client.GetAsync("api/authentication/verify-token");
            var responseBody = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = JObject.Parse(responseBody);
            Assert.Equal(userId, json["userId"]?.ToString());
            Assert.Equal(username, json["userName"]?.ToString());
        }

        [Fact(DisplayName = "[Cookie] GET /verify-token - Failure (No Cookie => Unauthorized)")]
        public async Task VerifyToken_NoCookie_ReturnsUnauthorized()
        {
            // Arrange: brand new client, no cookies
            var cleanClient = _fixture.Factory.CreateClient();

            // Act
            var response = await cleanClient.GetAsync("api/authentication/verify-token");

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region Refresh-Token Tests (Cookie-based)

        [Fact(DisplayName = "[Cookie] POST /refresh-token - Success (Valid Access/Refresh Tokens)")]
        public async Task RefreshToken_ValidTokens_ReturnsNewTokens()
        {
            // Arrange: register user and login once to get the valid JWT
            var username = $"refresh_{Guid.NewGuid()}";
            var password = "Password123!";
            var userId = await RegisterTestUserCookie(username, $"{username}@example.com", password);

            // Perform a single login call and capture the access token from the response.
            var loginDto = new UserLoginDto 
            { 
                Username = username, 
                Password = password 
            };
            var loginResponse = await _client.PostAsJsonAsync("api/authentication/login", loginDto);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

            var loginJson = JObject.Parse(await loginResponse.Content.ReadAsStringAsync());
            var oldAccessToken = loginJson["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(oldAccessToken), "Access token should not be null or empty.");

            // Clear the cookie so that it does not interfere with the refresh endpoint.
            var cookies = _fixture.CookieContainer.GetCookies(_client.BaseAddress);
            if (cookies["jwt"] != null)
            {
                cookies["jwt"].Expired = true;
            }
            // Alternatively, create a new HttpClient with no cookies
            var clientNoCookie = _fixture.Factory.CreateClient();

            // Manually create & store a valid refresh token for the user in the DB.
            string existingRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
                var token = new RefreshToken
                {
                    UserId = userId!,
                    Token = existingRefreshToken,
                    Expires = DateTime.UtcNow.AddMinutes(30),
                    CreatedAt = DateTime.UtcNow
                };
                ctx.RefreshTokens.Add(token);
                await ctx.SaveChangesAsync();
            }

            // Act: call the refresh endpoint using clientNoCookie
            var refreshDto = new RefreshTokenRequest
            {
                AccessToken = oldAccessToken!,
                RefreshToken = existingRefreshToken
            };
            var refreshResponse = await clientNoCookie.PostAsJsonAsync("api/authentication/refresh-token", refreshDto);
            var refreshBody = await refreshResponse.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

            var json = JObject.Parse(refreshBody);
            Assert.True(json.ContainsKey("accessToken"), "Response must contain 'accessToken'.");
            Assert.True(json.ContainsKey("refreshToken"), "Response must contain 'refreshToken'.");
            Assert.True(json.ContainsKey("expires"), "Response must contain 'expires'.");

            // Clean up the refresh token from the DB
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
                var tokenToRemove = await ctx.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == existingRefreshToken);
                if (tokenToRemove != null)
                {
                    ctx.RefreshTokens.Remove(tokenToRemove);
                    await ctx.SaveChangesAsync();
                }
            }
        }

        [Fact(DisplayName = "[Cookie] POST /refresh-token - Failure (Invalid Tokens => Unauthorized)")]
        public async Task RefreshToken_InvalidTokens_ReturnsUnauthorized()
        {
            // Arrange
            var refreshDto = new RefreshTokenRequest
            {
                AccessToken = "fakeAccess",
                RefreshToken = "fakeRefresh"
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/refresh-token", refreshDto);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact(DisplayName = "[Cookie] POST /refresh-token - Failure (Expired Refresh Token)")]
        public async Task RefreshToken_ExpiredRefreshToken_ReturnsUnauthorized()
        {
            // Arrange: register user, then create an expired refresh token
            var username = $"refresh_expired_{Guid.NewGuid()}";
            var password = "Password123!";
            var userId = await RegisterTestUserCookie(username, $"{username}@example.com", password);

            // Suppose we skip actual login, just assume the access token is valid for test
            string expiredToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
                var token = new RefreshToken
                {
                    UserId = userId!,
                    Token = expiredToken,
                    Expires = DateTime.UtcNow.AddMinutes(-1), // already expired
                    CreatedAt = DateTime.UtcNow.AddHours(-2)
                };
                ctx.RefreshTokens.Add(token);
                await ctx.SaveChangesAsync();
            }

            var refreshDto = new RefreshTokenRequest
            {
                AccessToken = "someAccessToken",
                RefreshToken = expiredToken
            };

            // Act
            var response = await _client.PostAsJsonAsync("api/authentication/refresh-token", refreshDto);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        // ------------------------------------------------------
        // SECTION 2: Additional Bearer-based Tests
        // ------------------------------------------------------
        #region Bearer Tests


       [Fact(DisplayName = "[Bearer] POST /register => Get token => use Bearer => verify-token => 200")]
        public async Task Bearer_RegisterAndVerify()
        {
            // 1) Register a user, capturing the token from JSON
            var username = $"bearer_reg_{Guid.NewGuid()}";
            var password = "BearerPass123!";
            var regDto = new UserRegistrationDto
            {
                Username = username,
                Email = $"bearer_{username}@example.com",
                Password = password
            };

            Console.WriteLine($"Registering user: {username}");
            // Use _client (or a new client) - doesn't matter for registration.
            var regResp = await _client.PostAsJsonAsync("api/authentication/register", regDto);
            Console.WriteLine($"Registration response status: {regResp.StatusCode}");

            Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

            var regBody = await regResp.Content.ReadAsStringAsync();

            var regJson = JObject.Parse(regBody);
            var bearerToken = regJson["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(bearerToken), "Should receive a 'token' in the register response.");

            // 2) Use a *new* client instance for Bearer tests
            var clientNoCookies = _fixture.Factory.CreateClient();

            // 3) Set the Authorization header using our helper method
            SetBearerToken(clientNoCookies, bearerToken); // Pass the client

            // 4) Call /verify-token => Should be OK
            Console.WriteLine("Making verify-token request with Authorization header");
            var verifyResp = await clientNoCookies.GetAsync("api/authentication/verify-token");
            Console.WriteLine($"Verify response status: {verifyResp.StatusCode}");

            // If there's an error response, print it
            if (verifyResp.StatusCode != HttpStatusCode.OK)
            {
                var errorContent = await verifyResp.Content.ReadAsStringAsync();
                Console.WriteLine($"Error response: {errorContent}");
            }

            Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

            var verifyBody = await verifyResp.Content.ReadAsStringAsync();
            Console.WriteLine($"Verify response body: {verifyBody}");

            var verifyJson = JObject.Parse(verifyBody);
            Assert.Equal(username, verifyJson["userName"]?.ToString());
        }




            
         [Fact(DisplayName = "[Bearer] POST /login => Bearer => verify-token => 200")]
        public async Task Bearer_LoginAndVerify()
        {
            // 1) Register
            var username = $"bearer_login_{Guid.NewGuid()}";
            var password = "BearerLogin123!";

            Console.WriteLine($"Registering user for login test: {username}");
            await RegisterTestUserCookie(username, $"{username}@example.com", password);

            // 2) Now login using clientNoCookies (we don't want any cookie set)
            var loginDto = new UserLoginDto
            {
                Username = username,
                Password = password
            };

            // Use a *new* client instance
            var clientNoCookies = _fixture.Factory.CreateClient();


            Console.WriteLine($"Logging in with user: {username}");
            var loginResp = await clientNoCookies.PostAsJsonAsync("api/authentication/login", loginDto);  // Use new client
            Console.WriteLine($"Login response status: {loginResp.StatusCode}");

            Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

            var loginBody = await loginResp.Content.ReadAsStringAsync();

            var loginJson = JObject.Parse(loginBody);
            var bearerToken = loginJson["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(bearerToken), "Login response must have 'token' field.");

            // 3) Set Bearer token using our helper method
            SetBearerToken(clientNoCookies, bearerToken); // Pass the client

            // 4) /verify-token => 200
            Console.WriteLine("Making verify-token request with Authorization header");
            var verifyResp = await clientNoCookies.GetAsync("api/authentication/verify-token");  // Use new client
            Console.WriteLine($"Verify response status: {verifyResp.StatusCode}");

            // If there's an error response, print it
            if (verifyResp.StatusCode != HttpStatusCode.OK)
            {
                var errorContent = await verifyResp.Content.ReadAsStringAsync();
                Console.WriteLine($"Error response: {errorContent}");
            }

            Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

            var verifyBody = await verifyResp.Content.ReadAsStringAsync();
            Console.WriteLine($"Verify response body: {verifyBody}");

            var verifyJson = JObject.Parse(verifyBody);
            Assert.Equal(username, verifyJson["userName"]?.ToString());
        }



         [Fact(DisplayName = "[Bearer] POST /refresh-token => using real token")]
        public async Task Bearer_RefreshToken_Flow()
        {
            // 1) Register
            var username = $"refresh_{Guid.NewGuid()}";
            var registerDto = new UserRegistrationDto
            {
                Username = username,
                Email = $"refresh_{username}@example.com",
                Password = "RefreshTest123!"
            };

            var registerResponse = await _client.PostAsJsonAsync("api/authentication/register", registerDto);
            Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
            var registerJson = JObject.Parse(await registerResponse.Content.ReadAsStringAsync());
            var initialToken = registerJson["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(initialToken), "Registration token should not be null or empty.");


            // 2) Generate Refresh token (login)
            var loginDto = new UserLoginDto
            {
                Username = username,
                Password = "RefreshTest123!"
            };
            var loginResponse = await _client.PostAsJsonAsync("api/authentication/login", loginDto);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var loginJson = JObject.Parse(await loginResponse.Content.ReadAsStringAsync());
            var refreshToken = loginJson["refreshToken"]?.ToString();
            var accessToken = loginJson["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(refreshToken), "Refresh token from login should not be null or empty.");
            Assert.False(string.IsNullOrEmpty(accessToken), "Access token from login should not be null or empty.");


            // 3) Use /refresh-token endpoint.
            // Use a new client instance for clarity and to avoid any lingering state
            using var refreshClient = _fixture.Factory.CreateClient();

            var refreshDto = new RefreshTokenRequest
            {
                AccessToken = accessToken, // Use the *actual* access token here.
                RefreshToken = refreshToken
            };

            var refreshResponse = await refreshClient.PostAsJsonAsync("api/authentication/refresh-token", refreshDto);
            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

            var refreshJson = JObject.Parse(await refreshResponse.Content.ReadAsStringAsync());
            var newAccessToken = refreshJson["accessToken"]?.ToString();
            var newRefreshToken = refreshJson["refreshToken"]?.ToString();

            Assert.False(string.IsNullOrEmpty(newAccessToken), "New access token should not be null or empty.");
            Assert.False(string.IsNullOrEmpty(newRefreshToken), "Refresh token should not be null or empty."); // Corrected assertion
        }



        #endregion

        // ------------------------------------------------------
        // SECTION 3: Helper Methods
        // ------------------------------------------------------

        /// <summary>
        /// Registers a user using the /register endpoint (COOKIE client), asserting success, returns userId.
        /// </summary>
        private async Task<string?> RegisterTestUserCookie(string username, string email, string password)
        {
            var dto = new UserRegistrationDto
            {
                Username = username,
                Email = email,
                Password = password
            };
            var response = await _client.PostAsJsonAsync("api/authentication/register", dto);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);
            return json["userId"]?.ToString();
        }

        private void SetBearerToken(HttpClient client, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentException("Token cannot be null or empty.", nameof(token));
            }

            // Corrected logging: use numbered placeholders instead of named ones
            Console.WriteLine("Setting Bearer token: {0}", token);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Console.WriteLine("Authorization header set. Scheme: {0}, Parameter: {1}",
                client.DefaultRequestHeaders.Authorization.Scheme,
                client.DefaultRequestHeaders.Authorization.Parameter);
        }


   

/// <summary>
/// Logs in a user using /login endpoint (COOKIE client), asserting success.
/// </summary>
private async Task LoginTestUserCookie(string username, string password)
{
    var dto = new UserLoginDto
    {
        Username = username,
        Password = password
    };
    // _client is created with CreateClientWithCookies(), which should share a CookieContainer
    var response = await _client.PostAsJsonAsync("api/authentication/login", dto);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    // REMOVE the manual Set-Cookie parsing and adding to _fixture.CookieContainer.
    // The CookieContainer associated with _client should automatically pick up
    // the Set-Cookie header from the login response.

    // For debugging, you can check if the cookie container has the cookie:
    var cookies = _fixture.CookieContainer.GetCookies(_client.BaseAddress!); // Ensure BaseAddress is not null
    var jwtCookie = cookies["jwt"];
    if (jwtCookie != null)
    {
        Console.WriteLine($"[DEBUG] After Login - JWT Cookie: Name={jwtCookie.Name}, Value={jwtCookie.Value}, Path={jwtCookie.Path}, Domain={jwtCookie.Domain}, Expires={jwtCookie.Expires}");
    }
    else
    {
        Console.WriteLine("[DEBUG] After Login - JWT Cookie NOT FOUND in container.");
        // You might want to inspect response.Headers to see if Set-Cookie was even sent
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            foreach(var header in setCookieHeaders)
            {
                Console.WriteLine($"[DEBUG] Set-Cookie Header from Login: {header}");
            }
        }
    }
}


// Helper method to parse the Set-Cookie header
private Cookie ParseSetCookieHeader(string header, Uri baseAddress)
{
    try
    {
        // Basic parsing (improved from before, but still not 100% RFC compliant).
        // A full RFC 6265 parser would be even better, but this is a good compromise.
        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var nameValue = parts[0].Split('=', 2);
        var name = nameValue[0].Trim();
        var value = nameValue[1].Trim();

        var cookie = new Cookie(name, value);
        cookie.Domain = baseAddress.Host;  // *Always* set the domain explicitly
        cookie.Path = "/"; // Default path

        foreach (var part in parts.Skip(1))
        {
            var trimmedPart = part.Trim();
            if (trimmedPart.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(trimmedPart.Substring("expires=".Length), out var expires))
                {
                    cookie.Expires = expires.UtcDateTime;
                }
            }
            else if (trimmedPart.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
            {
                cookie.Path = trimmedPart.Substring("path=".Length);
            }
            else if (trimmedPart.StartsWith("domain=", StringComparison.OrdinalIgnoreCase))
            {
                cookie.Domain = trimmedPart.Substring("domain=".Length);
            }
            else if (trimmedPart.Equals("secure", StringComparison.OrdinalIgnoreCase))
            {
                cookie.Secure = true;
            }
            else if (trimmedPart.Equals("httponly", StringComparison.OrdinalIgnoreCase))
            {
                cookie.HttpOnly = true;
            }
            // You could add parsing for SameSite here if needed.
        }
        return cookie;

    }
    catch (Exception ex)
    {
        Console.WriteLine("Error parsing Set-Cookie header: {0}. Exception: {1}", header, ex);
        return null; // Indicate parsing failure
    }
}
    }
}
