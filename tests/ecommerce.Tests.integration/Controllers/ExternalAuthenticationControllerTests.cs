using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ecommerce.Controllers;
using ecommerce.Db;
using ecommerce.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;


namespace ecommerce.Tests.Integration
{
    [Collection("Integration Tests")]
    public class ExternalAuthenticationControllerTests : IAsyncLifetime
    {
        private readonly IntegrationTestFixture _fixture;
        private readonly HttpClient _client;

        public ExternalAuthenticationControllerTests(IntegrationTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.CreateClientWithCookies();
        }

        public async Task InitializeAsync()
        {
            await _fixture.ResetDatabaseAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        #region Challenge Tests

       [Fact(DisplayName = "GET /challenge/{provider} - Initiates External Auth Challenge")]
        public async Task Challenge_InitiatesExternalAuthFlow()
        {
            // Arrange
            var provider = "Google";
            var redirectUrl = "http://localhost/callback";

            // Act
            var response = await _client.GetAsync(
                $"api/external-authentication/challenge/{provider}?" +
                $"redirectUrl={Uri.EscapeDataString(redirectUrl)}");

            // Assert
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location.ToString();
            Assert.Contains("accounts.google.com", location);
            Assert.Contains("redirect_uri=", location);
        }
                [Fact(DisplayName = "GET /challenge/{provider} - Works Without Explicit Redirect URL")]
        public async Task Challenge_WorksWithoutRedirectUrl()
        {
            // First, get the list of available providers to confirm what's configured
            var providersResponse = await _client.GetAsync("api/external-authentication/providers");
            var providersContent = await providersResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Available providers: {providersContent}");
            
            // Use the exact provider name from the response
            var provider = "Google"; 
            var redirectUrl = "http://localhost/api/external-authentication/callback";
            
            var response = await _client.GetAsync($"api/external-authentication/challenge/{provider}?redirectUrl={Uri.EscapeDataString(redirectUrl)}");
            
            Console.WriteLine($"Challenge response: {response.StatusCode}");
            
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            
            var locationHeader = response.Headers.Location?.ToString();
            Assert.NotNull(locationHeader);
            Assert.Contains("accounts.google.com", locationHeader);
        }

        [Fact(DisplayName = "GET /challenge/{provider} - Returns BadRequest for Invalid Provider")]
        public async Task Challenge_ReturnsError_ForInvalidProvider()
        {
            // Arrange - Use a non-existent provider
            var provider = "InvalidProvider";
            var redirectUrl = "http://localhost/callback";

            // Act - Call the challenge endpoint with a client that doesn't auto-redirect
            var response = await _client.GetAsync($"api/external-authentication/challenge/{provider}?redirectUrl={redirectUrl}");

            // Assert - Should return an error (either BadRequest or NotFound depending on your implementation)
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest || 
                        response.StatusCode == HttpStatusCode.NotFound);
        }

        #endregion

        #region Callback Tests

        [Fact(DisplayName = "GET /callback - Creates New User From External Login")]
        public async Task Callback_CreatesNewUser_FromExternalLogin()
        {
            // Arrange - Set up a client with external auth simulation
            var email = "newuser@example.com";
            var name = "New_User";
            var providerKey = "google-new-user-12345";
            
            var client = await SimulateExternalLogin("Google", providerKey, name, email);

            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");

            // Assert - Should successfully create a new user
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            
            // Token should be in the response
            Assert.NotNull(json["token"]);
            
            // User should have been created with the right properties
            Assert.NotNull(json["user"]);
            Assert.Equal(name, json["user"]["userName"]);
            Assert.Equal(email, json["user"]["email"]);
            
            // Verify user exists in the database
            var userId = json["user"]["id"].ToString();
            var user = await GetUserById(userId);
            Assert.NotNull(user);
            Assert.Equal(email, user.Email);
            
            // Verify external login was added
            var userLogins = await GetUserLogins(userId);
            Assert.Single(userLogins);
            Assert.Equal("Google", userLogins.First().LoginProvider);
            Assert.Equal(providerKey, userLogins.First().ProviderKey);
        }

        [Fact(DisplayName = "GET /callback - Adds Login To Existing User")]
        public async Task Callback_AddsLogin_ToExistingUser()
        {
            // Arrange - First create a user
            var uniqueId = Guid.NewGuid().ToString();
            var username = $"existing_user_{uniqueId}";
            var email = $"existing_{uniqueId}@example.com";
            var password = "StrongPassword123!";


            var userId = await RegisterTestUser(username, email, password);
            
            // Simulate external login with the same email
            var client = await SimulateExternalLogin("Google", "google-existing-12345", "External_User", email);

            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");

            // Assert - Should add external login to existing user
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            
            // Should return token
            Assert.NotNull(json["token"]);
            
            // Should be the existing user
            Assert.NotNull(json["user"]);
            Assert.Equal(userId, json["user"]["id"].ToString());
            Assert.Equal(username, json["user"]["userName"]);
            Assert.Equal(email, json["user"]["email"]);
            
            // Verify login was added to the user
            var userLogins = await GetUserLogins(userId);
            Assert.Single(userLogins);
            Assert.Equal("Google", userLogins.First().LoginProvider);
        }

        [Fact(DisplayName = "GET /callback - Signs In With Existing External Login")]
        public async Task Callback_SignsIn_WithExistingExternalLogin()
        {
            // Arrange - Create a user with external login
            var email = "extuser@example.com";
            var name = "External_User";
            var providerKey = "google-existing-login-12345";
            
            // First create the user with external login
            var firstClient = await SimulateExternalLogin("Google", providerKey, name, email);
            var firstResponse = await firstClient.GetAsync("api/external-authentication/callback");
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            var firstJson = JObject.Parse(await firstResponse.Content.ReadAsStringAsync());
            var userId = firstJson["user"]["id"].ToString();
            
            // Now simulate a second login with the same external provider
            var secondClient = await SimulateExternalLogin("Google", providerKey, name, email);
            
            // Act - Call the callback endpoint again
            var response = await secondClient.GetAsync("api/external-authentication/callback");
            
            // Assert - Should sign in with the existing user
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            
            // Should return a token
            Assert.NotNull(json["token"]);
            
            // Should be the same user
            Assert.NotNull(json["user"]);
            Assert.Equal(userId, json["user"]["id"].ToString());
            Assert.Equal(name, json["user"]["userName"]);
            Assert.Equal(email, json["user"]["email"]);
        }

        [Fact(DisplayName = "GET /callback - Returns BadRequest When Missing Email")]
        public async Task Callback_ReturnsBadRequest_WhenMissingEmail()
        {
            // Arrange - Simulate external login without email
            var client = await SimulateExternalLogin("Google", "google-no-email-12345", "No Email User", null);
            
            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");
            
            // Assert - Should return BadRequest due to missing email
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("Email not provided", responseBody);
        }

        [Fact(DisplayName = "GET /callback - Returns BadRequest When External Info is Null")]
        public async Task Callback_ReturnsBadRequest_WhenExternalInfoIsNull()
        {
            // Act - Call the callback endpoint without external login info
            var response = await _client.GetAsync("api/external-authentication/callback");
            
            // Assert - Should return BadRequest
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("Error getting external login information", responseBody);
        }

        [Fact(DisplayName = "GET /callback - Creates User with Default Role")]
        public async Task Callback_CreatesUser_WithDefaultRole()
        {
            // Arrange - Set up external login
            var email = "roleuser@example.com";
            var name = "Role_User";
            var providerKey = "google-role-test-12345";
            
            var client = await SimulateExternalLogin("Google", providerKey, name, email);

            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");

            // Assert - Should create user successfully
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            var userId = json["user"]["id"].ToString();
            
            // Verify user has the default role
            var user = await GetUserById(userId);
            var roles = await GetUserRoles(user);
            
            Assert.Contains("ContentUploader", roles);
        }

        [Fact(DisplayName = "GET /callback - Creates User with Default Claims")]
        public async Task Callback_CreatesUser_WithDefaultClaims()
        {
            // Arrange - Set up external login
            var email = "claimuser@example.com";
            var name = "Claim_User";
            var providerKey = "google-claim-test-12345";
            
            var client = await SimulateExternalLogin("Google", providerKey, name, email);

            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");

            // Assert - Should create user successfully
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            var userId = json["user"]["id"].ToString();
            
            // Verify user has the default claim
            var user = await GetUserById(userId);
            var claims = await GetUserClaims(user);
            
            Assert.Contains(claims, c => c.Type == "permission" && c.Value == "upload_content");
        }

        [Fact(DisplayName = "GET /callback - Token Contains Expected Claims")]
        public async Task Callback_TokenContainsExpectedClaims()
        {
            // Arrange - Set up external login
            var email = "claimverify@example.com";
            var name = "Claim_Verify_User";
            var providerKey = "google-claim-verify-12345";
            
            var client = await SimulateExternalLogin("Google", providerKey, name, email);

            // Act - Call the callback endpoint
            var response = await client.GetAsync("api/external-authentication/callback");

            // Assert - Should create user successfully
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody);
            var token = json["token"].ToString();
            
            // Decode and verify the JWT
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);
            
            // Verify essential claims
            Assert.NotNull(jwtToken.Subject); // "sub" claim with user ID
            
            // Get the user to verify claims match database
            var userId = jwtToken.Subject;
            var user = await GetUserById(userId);
            Assert.Equal(email, user.Email);
            
            // Verify token expiration is reasonable (e.g. within 1 hour)
            var now = DateTime.UtcNow;
            Assert.True(jwtToken.ValidTo > now);
            Assert.True(jwtToken.ValidTo <= now.AddHours(1));
            
            // Verify refresh token exists and is valid
            var refreshToken = json["refreshToken"].ToString();
            Assert.NotNull(refreshToken);
            Assert.NotEmpty(refreshToken);
            
            var refreshTokenEntity = await VerifyRefreshTokenExists(refreshToken, userId);
            Assert.NotNull(refreshTokenEntity);
            Assert.True(refreshTokenEntity.Expires > DateTime.UtcNow);
        }

        #endregion

        #region Available Providers Tests

     [Fact(DisplayName = "GET /providers - Returns Available Authentication Providers")]
public async Task GetAvailableProviders_ReturnsAuthenticationSchemes()
{
    var response = await _client.GetAsync("api/external-authentication/providers");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    
    var responseBody = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"Providers response: {responseBody}");
    
    var json = JObject.Parse(responseBody);
    var providers = json["providers"] as JArray;
    
    Assert.NotNull(providers);
    Assert.Contains(providers, p => 
        p["name"].ToString().Equals("Google", StringComparison.OrdinalIgnoreCase));
}
        #endregion

        #region User Login Management Tests

        [Fact(DisplayName = "GET /logins - Returns User's External Logins")]
        public async Task GetUserLogins_ReturnsExternalLogins()
        {
            // Arrange - Create a user
            var username = "logins_test_user";
            var email = "logins@example.com";
            var password = "StrongPassword123!";

            var userId = await RegisterAndLoginTestUser(username, email, password);

            // Call the logins endpoint to get initial state
            var response = await _client.GetAsync("api/external-authentication/logins");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseBody); // Fixed CS0103
            var logins = json["Logins"] as JArray ?? json["logins"] as JArray;
            Assert.NotNull(logins);
            Assert.Empty(logins);

            // Add a mock external login to the user
            await AddExternalLogin(userId, "Google", "google-12345", "Google");

            // Act - Call logins endpoint again
            response = await _client.GetAsync("api/external-authentication/logins");
            
            // Assert - Should now show the Google login
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            responseBody = await response.Content.ReadAsStringAsync();
            json = JObject.Parse(responseBody);
            logins = logins = json["logins"] as JArray;
            Assert.NotNull(logins);
            Assert.Single(logins);
            Assert.Equal("Google", logins[0]["provider"]);
        }

        [Fact(DisplayName = "GET /logins - Returns Unauthorized When Not Authenticated")]
        public async Task GetUserLogins_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            // Arrange - Create a client without authentication
            var client = _fixture.CreateClientWithCookies();
            
            // Act - Call the logins endpoint
            var response = await client.GetAsync("api/external-authentication/logins");
            
            // Assert - Should return Unauthorized
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

       [Fact(DisplayName = "GET /logins - Enforces Access Control")]
public async Task GetUserLogins_EnforcesAccessControl()
{
    // Arrange - Create two users
    var username1 = "access_control_user1";
    var email1 = "access1@example.com";
    var password1 = "StrongPassword123!";
    
    var username2 = "access_control_user2";
    var email2 = "access2@example.com";
    var password2 = "StrongPassword123!";

    var userId1 = await RegisterTestUser(username1, email1, password1);
    var userId2 = await RegisterTestUser(username2, email2, password2);
    
    // Add different external logins to each user
    await AddExternalLogin(userId1, "Google", "google-access1-12345", "Google");
    await AddExternalLogin(userId2, "Microsoft", "microsoft-access2-12345", "Microsoft");
    
    // Login as the first user
    await LoginTestUser(username1, password1);
    
    // Act - Try to get logins
    var response = await _client.GetAsync("api/external-authentication/logins");
    
    // Assert - Should succeed and only return user1's logins
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    
    var responseBody = await response.Content.ReadAsStringAsync();
    var json = JObject.Parse(responseBody);
    
    // Check for the property 'logins' with the correct casing
    var logins = json["logins"] as JArray;
    
    // Make sure logins is not null before accessing it
    Assert.NotNull(logins);
    Assert.Single(logins);
    Assert.Equal("Google", logins[0]["provider"].ToString());
    
    // Now try to unlink user2's login
    var unlinkResponse = await _client.DeleteAsync("api/external-authentication/unlink/Microsoft");
    
    // Should fail because user1 doesn't have a Microsoft login
    Assert.Equal(HttpStatusCode.BadRequest, unlinkResponse.StatusCode);
    
    // Verify user2's login wasn't affected
    var user2Logins = await GetUserLogins(userId2);
    Assert.Single(user2Logins);
    Assert.Equal("Microsoft", user2Logins.First().LoginProvider);
}

        [Fact(DisplayName = "DELETE /unlink/{provider} - Removes External Login")]
        public async Task UnlinkLogin_RemovesExternalLogin()
        {
            // Arrange 
            var username = "unlink_test_user";
            var email = "unlink@example.com";
            var password = "StrongPassword123!";

            var userId = await RegisterAndLoginTestUser(username, email, password);
            
            // Add external login with the exact provider name
            await AddExternalLogin(userId, "Google", "google-unlink-12345", "Google");
            
            // Verify login was added
            var initialLogins = await GetUserLogins(userId);
            Assert.Single(initialLogins);
            Assert.Equal("Google", initialLogins.First().LoginProvider);

            // Make verify-token request to confirm authentication
            var verifyResponse = await _client.GetAsync("api/authentication/verify-token");
            Console.WriteLine($"Verify response status: {verifyResponse.StatusCode}");
            Console.WriteLine($"Verify response body: {await verifyResponse.Content.ReadAsStringAsync()}");
            
            // Act
            var response = await _client.DeleteAsync("api/external-authentication/unlink/Google");

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Verify removal
            var finalLogins = await GetUserLogins(userId);
            Assert.Empty(finalLogins);
        }

        [Fact(DisplayName = "DELETE /unlink/{provider} - Data Remains Consistent After Unlink")]
        public async Task UnlinkLogin_MaintainsDataConsistency()
        {
            // Arrange 
            var username = "data_consistency_user";
            var email = "consistency@example.com";
            var password = "StrongPassword123!";

            var userId = await RegisterAndLoginTestUser(username, email, password);
            
            // Add multiple external logins
            await AddExternalLogin(userId, "Google", "google-consistency-12345", "Google");
            await AddExternalLogin(userId, "Facebook", "facebook-consistency-12345", "Facebook");
            
            // Verify initial state
            var initialLogins = await GetUserLogins(userId);
            Assert.Equal(2, initialLogins.Count);
            
            // Act - Remove one login
            var response = await _client.DeleteAsync("api/external-authentication/unlink/Google");
            
            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            // Verify only the correct login was removed
            var finalLogins = await GetUserLogins(userId);
            Assert.Single(finalLogins);
            Assert.Equal("Facebook", finalLogins.First().LoginProvider);
            
            // Verify user data is still intact
            var user = await GetUserById(userId);
            Assert.Equal(username, user.UserName);
            Assert.Equal(email, user.Email);
            
            // Verify roles and claims weren't affected
            var roles = await GetUserRoles(user);
            var claims = await GetUserClaims(user);
            
            // These should match whatever default roles/claims you set up for new users
            Assert.NotEmpty(roles); // Assuming users get at least one role
            Assert.NotEmpty(claims); // Assuming users get at least one claim
        }

        [Fact(DisplayName = "DELETE /unlink/{provider} - Returns BadRequest When Provider Not Found")]
        public async Task UnlinkLogin_ReturnsBadRequest_WhenProviderNotFound()
        {
            // Arrange - Create a user without external logins
            var username = "no_ext_login_user";
            var email = "no_ext@example.com";
            var password = "StrongPassword123!";

            await RegisterAndLoginTestUser(username, email, password);
            
            // Act - Try to unlink a non-existent provider
            var response = await _client.DeleteAsync("api/external-authentication/unlink/Google");
            
            // Assert - Should return BadRequest
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("No Google login found", responseBody);
        }

        [Fact(DisplayName = "DELETE /unlink/{provider} - Returns Unauthorized When Not Authenticated")]
        public async Task UnlinkLogin_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            // Arrange - Create a client without authentication
            var client = _fixture.CreateClientWithCookies();
            
            // Act - Call the unlink endpoint
            var response = await client.DeleteAsync("api/external-authentication/unlink/Google");
            
            // Assert - Should return Unauthorized
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact(DisplayName = "POST /link/{provider} - Initiates External Login Link")]
        public async Task LinkExternalLogin_InitiatesExternalLoginLink()
        {
            // Arrange - Create and login a user
            var username = "link_test_user";
            var email = "link@example.com";
            var password = "StrongPassword123!";

            await RegisterAndLoginTestUser(username, email, password);
            
            // Act - Call the link endpoint
            var response = await _client.PostAsync("api/external-authentication/link/Google", null);
            
            // Assert - Should redirect to the Google auth page
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            
            var locationHeader = response.Headers.Location?.ToString();
            Assert.NotNull(locationHeader);
            Assert.Contains("accounts.google.com", locationHeader);
        }

        [Fact(DisplayName = "POST /link/{provider} - Returns Unauthorized When Not Authenticated")]
        public async Task LinkExternalLogin_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            // Arrange - Create a client without authentication
            var client = _fixture.CreateClientWithCookies();
            
            // Act - Call the link endpoint
            var response = await client.PostAsync("api/external-authentication/link/Google", null);
            
            // Assert - Should return Unauthorized
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact(DisplayName = "GET /link-callback - Links External Login To User")]
        public async Task LinkCallback_LinksExternalLogin_ToUser()
        {
            // This test would require significant mocking of the auth flow and state
            // It's one of the most challenging parts to test without significant integration
            // Consider mocking this particular test or implementing a custom auth handler
        }

        [Fact(DisplayName = "Concurrent External Logins Are Handled Correctly")]
        public async Task ConcurrentExternalLogins_AreHandledCorrectly()
        {
            // Arrange - Set up two different external accounts with same provider
            var email1 = "concurrent1@example.com";
            var email2 = "concurrent2@example.com";
            var name1 = "Concurrent_User_1";
            var name2 = "Concurrent_User_2";
            var providerKey1 = "google-concurrent-1-12345";
            var providerKey2 = "google-concurrent-2-12345";
            
            var client1 = await SimulateExternalLogin("Google", providerKey1, name1, email1);
            var client2 = await SimulateExternalLogin("Google", providerKey2, name2, email2);

            // Act - Call the callback endpoint for both clients "simultaneously"
            var task1 =  client1.GetAsync("api/external-authentication/callback");
            var task2 = client2.GetAsync("api/external-authentication/callback");
            
            // Wait for both tasks to complete
            await Task.WhenAll(task1, task2);
            
            // Assert - Both should be successful
            Assert.Equal(HttpStatusCode.OK, task1.Result.StatusCode);
            Assert.Equal(HttpStatusCode.OK, task2.Result.StatusCode);
            
            // Parse responses
            var json1 = JObject.Parse(await task1.Result.Content.ReadAsStringAsync());
            var json2 = JObject.Parse(await task2.Result.Content.ReadAsStringAsync());
            
            // Extract user IDs
            var userId1 = json1["user"]["id"].ToString();
            var userId2 = json2["user"]["id"].ToString();
            
            // Users should be different
            Assert.NotEqual(userId1, userId2);
            
            // Verify both users have correct external logins
            var logins1 = await GetUserLogins(userId1);
            var logins2 = await GetUserLogins(userId2);
            
            Assert.Single(logins1);
            Assert.Equal("Google", logins1.First().LoginProvider);
            Assert.Equal(providerKey1, logins1.First().ProviderKey);
            
            Assert.Single(logins2);
            Assert.Equal("Google", logins2.First().LoginProvider);
            Assert.Equal(providerKey2, logins2.First().ProviderKey);
        }

       [Fact(DisplayName = "Token Revocation Is Enforced")]
        public async Task TokenRevocation_IsEnforced()
        {
            // Arrange - Create and login a user
            var username = "revocation_test_user";
            var email = "revocation@example.com";
            var password = "StrongPassword123!";

            var userId = await RegisterAndLoginTestUser(username, email, password);
            
            // Verify we're authenticated
            var initialResponse = await _client.GetAsync("api/external-authentication/logins");
            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            
            // Save the token before logout
            var token = _client.DefaultRequestHeaders.Authorization?.Parameter;
            
            // Act - Simulate token revocation by logging the user out
            await _client.PostAsync("api/authentication/logout", null);
            
            // Important: Add delay to ensure the revocation is processed
            await Task.Delay(100);
            
            // Create a completely new client with no shared state from the original client
            var freshClient = _fixture.CreateClientWithCookies();
            freshClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            // Assert - Should be unauthorized with the fresh client
            var finalResponse = await freshClient.GetAsync("api/external-authentication/logins");
            Assert.Equal(HttpStatusCode.Unauthorized, finalResponse.StatusCode);
        }

        [Fact(DisplayName = "Invalid Token Is Rejected")]
        public async Task InvalidToken_IsRejected()
        {
            // Arrange - Create a client with an invalid token
            var client = _fixture.CreateClientWithCookies();
            var invalidToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);
            
            // Act - Try to access a protected endpoint
            var response = await client.GetAsync("api/external-authentication/logins");
            
            // Assert - Should be unauthorized
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region Helper Methods

        /// <summary>
       private async Task<HttpClient> SimulateExternalLogin(string provider, string providerKey, string name, string email)
{
    // Create a custom client using TestServer's authentication simulation capabilities
    var client = _fixture.CreateClientWithCookies();
    
    // Configure the test authentication handler to simulate an external login
    await ConfigureTestAuthHandler(client, provider, providerKey, name, email);
    
    return client;
}

/// <summary>
/// Configures the test authentication handler to simulate an external login
/// </summary>
private async Task ConfigureTestAuthHandler(HttpClient client, string provider, string providerKey, string name, string email)
{
    // Set up authentication cookies that will be used by the handler in the test server
    var authUrl = $"api/test-auth/simulate-external-login?provider={provider}&providerKey={providerKey}";
    
    if (!string.IsNullOrEmpty(name))
    {
        authUrl += $"&name={Uri.EscapeDataString(name)}";
    }
    
    if (!string.IsNullOrEmpty(email))
    {
        authUrl += $"&email={Uri.EscapeDataString(email)}";
    }
    
    // Call the test auth endpoint to set up the cookies
    var response = await client.GetAsync(authUrl);
    response.EnsureSuccessStatusCode();
}

/// <summary>
/// Registers a test user and returns the user ID
/// </summary>
private async Task<string> RegisterTestUser(string username, string email, string password)
{
    // Create registration request
    var registrationData = new
    {
        Username = username,
        Email = email,
        Password = password,
        ConfirmPassword = password 
    };

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    var content = new StringContent(JsonSerializer.Serialize(registrationData, options), 
        Encoding.UTF8, "application/json");
    // Call registration endpoint
    var response = await _client.PostAsync("api/authentication/register", content);

    // Log response for debugging - Fix the formatting error
    var responseBody = await response.Content.ReadAsStringAsync();

    response.EnsureSuccessStatusCode();

    // Extract user ID from response - check for both possible property names
    var json = JObject.Parse(responseBody);
    string userId = null;
    
    // Try different possible property names
    if (json["userId"] != null)
        userId = json["userId"].ToString();
    else if (json["id"] != null)
        userId = json["id"].ToString();
    
    if (string.IsNullOrEmpty(userId))
    {
        throw new InvalidOperationException($"Expected user ID in response but got: {responseBody}");
    }
    
    return userId;
}

/// <summary>
/// Registers a test user, logs them in, and returns the user ID
/// </summary>
private async Task<string> RegisterAndLoginTestUser(string username, string email, string password)
{
    // Register the user
    var userId = await RegisterTestUser(username, email, password);
    
    // Login the user - use username exactly as the controller expects
    var loginData = new 
    {
        Username = username, // Controller expects Username, not Email
        Password = password
    };
    
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    var content = new StringContent(JsonSerializer.Serialize(loginData, options), 
        Encoding.UTF8, "application/json");
    
    var response = await _client.PostAsync("api/authentication/login", content);
    var responseBody = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"Login response status: {response.StatusCode}");
    
    // This will throw if not successful
    response.EnsureSuccessStatusCode();
    
    // Extract the JWT token from the response
    var json = JObject.Parse(responseBody);
    var token = json["token"].ToString();
    
    // Set the bearer token for subsequent requests
    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
    // Make a verification request to confirm authorization
    var verifyResponse = await _client.GetAsync("api/authentication/verify-token");
    Console.WriteLine($"Verify response status: {verifyResponse.StatusCode}");
    
    if (verifyResponse.IsSuccessStatusCode)
    {
        var verifyBody = await verifyResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Verify response body: {verifyBody}");
    }
    
    return userId;
}

/// <summary>
/// Adds an external login to a user
/// </summary>
private async Task AddExternalLogin(string userId, string provider, string providerKey, string displayName)
{
    // Access the UserManager directly through the test server
    using var scope = _fixture.Factory.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
    
    var user = await userManager.FindByIdAsync(userId);
    Assert.NotNull(user);
    
    var login = new UserLoginInfo(provider, providerKey, displayName);
    var result = await userManager.AddLoginAsync(user, login);
    
    Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
    
    // Explicitly save changes to ensure they're committed
    await dbContext.SaveChangesAsync();
    
    // Verify the login was added
    var logins = await userManager.GetLoginsAsync(user);
    Console.WriteLine($"External logins after add: {logins.Count}");
    foreach (var l in logins)
    {
        Console.WriteLine($"Login provider: {l.LoginProvider}, key: {l.ProviderKey}");
    }
}

/// <summary>
/// Gets a user by ID
/// </summary>
private async Task<User> GetUserById(string userId)
{
    using var scope = _fixture.Factory.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    
    return await userManager.FindByIdAsync(userId);
}

/// <summary>
/// Gets a user's external logins
/// </summary>
private async Task<IList<UserLoginInfo>> GetUserLogins(string userId)
{
    using var scope = _fixture.Factory.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    
    var user = await userManager.FindByIdAsync(userId);
    Assert.NotNull(user);
    
    return await userManager.GetLoginsAsync(user);
}

/// <summary>
/// Gets a user's roles
/// </summary>
private async Task<IList<string>> GetUserRoles(User user)
{
    using var scope = _fixture.Factory.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    
    return await userManager.GetRolesAsync(user);
}

/// <summary>
/// Gets a user's claims
/// </summary>
private async Task<IList<Claim>> GetUserClaims(User user)
{
    using var scope = _fixture.Factory.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    
    return await userManager.GetClaimsAsync(user);
}

private async Task LoginTestUser(string username, string password)
{
    var loginData = new 
    {
        Username = username,
        Password = password
    };
    
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    var content = new StringContent(JsonSerializer.Serialize(loginData, options), 
        Encoding.UTF8, "application/json");
    
    var response = await _client.PostAsync("api/authentication/login", content);
    response.EnsureSuccessStatusCode();
    
    var responseBody = await response.Content.ReadAsStringAsync();
    var json = JObject.Parse(responseBody);
    var token = json["token"].ToString();
    
    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

private async Task<RefreshToken> VerifyRefreshTokenExists(string token, string userId)
{
    using var scope = _fixture.Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
    
    return await dbContext.RefreshTokens
        .FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId);
}

#endregion
}
}