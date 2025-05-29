#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers; // Required for AuthenticationHeaderValue
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Stripe;
using ecommerce.Tests; 
using ecommerce.Dto;
using ecommerce.Infrastructure.Repository.Interfaces; 
using ecommerce.Helpers; 
using ecommerce.Db;
using ecommerce.Models;
 
using Newtonsoft.Json.Linq;
using System.Linq;
/*
namespace ecommerce.Tests.IntegrationTests.Controllers
{
    [Collection("Integration Tests")]
    public class SubscriptionControllerTests : IAsyncLifetime
    {
        private HttpClient _client; // Made non-readonly to initialize in InitializeAsync
        private readonly IntegrationTestFixture _fixture;
        private readonly string _testUserId = "test_user_123"; 
        private readonly string _validPriceId = "price_valid_123"; 
        private readonly string _invalidPriceId = "price_invalid_999";

        // Define a simple DTO to deserialize the token response
        private class TestTokenResponseDto
        {
            public string? Token { get; set; }
            public string? UserId { get; set; }
            public string? UserName { get; set; }
            public DateTime Expires { get; set; }
        }

        public SubscriptionControllerTests(IntegrationTestFixture fixture)
        {
            _fixture = fixture;
            // Initialize a plain client here; authorization will be set in InitializeAsync
            _client = _fixture.Factory.CreateClient(); 
        }

        public async Task InitializeAsync()
        {
            await _fixture.ResetDatabaseAsync();
            await SeedTestDataAsync();

            // --- Corrected Authentication Setup ---
            // Use a temporary client or the existing _client (before auth is set)
            // to call the TestAuthController to get a valid token.
            // TestAuthController.GenerateTestToken is assumed not to require prior authorization.
            var tokenResponse = await _client.GetAsync($"/api/test-auth/generate-test-token?userId={Uri.EscapeDataString(_testUserId)}&userName={Uri.EscapeDataString(_testUserId)}");
            
            Console.WriteLine($"DEBUG - Token Generation for SubscriptionTests - Status Code: {tokenResponse.StatusCode}");
            var tokenResponseBody = await tokenResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Token Generation for SubscriptionTests - Response Body: {tokenResponseBody}");

            tokenResponse.EnsureSuccessStatusCode(); // Ensure token generation was successful

            // Using Newtonsoft.Json.Linq for simple parsing here, System.Text.Json could also be used.
            var tokenJson = JObject.Parse(tokenResponseBody);
            var actualToken = tokenJson["token"]?.ToString();

            if (string.IsNullOrEmpty(actualToken))
            {
                throw new InvalidOperationException($"Failed to retrieve a valid token for test user '{_testUserId}'. Response: {tokenResponseBody}");
            }
            
            Console.WriteLine($"DEBUG - Token Generation for SubscriptionTests - Retrieved Token (first 10 chars): {actualToken.Substring(0, Math.Min(actualToken.Length, 10))}...");

            // Now set the authorization header for the _client that will be used by tests
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actualToken);
            Console.WriteLine($"DEBUG - Client for SubscriptionControllerTests is now authorized for user: {_testUserId}");
            // --- End Corrected Authentication Setup ---
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task GetSubscriptionStatus_NoSubscription_ReturnsFalse()
        {
            // Act
            var response = await _client.GetAsync("/api/subscription/status");

            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(GetSubscriptionStatus_NoSubscription_ReturnsFalse)} - Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(GetSubscriptionStatus_NoSubscription_ReturnsFalse)} - Response Body: {responseBody}");
            // *** END LOGGING ***

            // Assert
            response.EnsureSuccessStatusCode(); 
            var content = await response.Content.ReadFromJsonAsync<SubscriptionStatusResponseDto>();
            Assert.NotNull(content); // Ensure content is not null before accessing IsSubscribed
            Assert.False(content.IsSubscribed);
        }

        [Fact]
        public async Task CreateCheckoutSession_ValidRequest_ReturnsSessionId()
        {
            // Arrange
            var request = new SubscriptionRequest { PriceId = _validPriceId };

            // Act
            var response = await _client.PostAsJsonAsync("/api/subscription/create-checkout-session", request);

            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(CreateCheckoutSession_ValidRequest_ReturnsSessionId)} - Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(CreateCheckoutSession_ValidRequest_ReturnsSessionId)} - Response Body: {responseBody}");
            // *** END LOGGING ***

            // Assert
            response.EnsureSuccessStatusCode(); 
            var content = await response.Content.ReadFromJsonAsync<CreateCheckoutSessionResponseDto>();
            Assert.NotNull(content);
            Assert.NotNull(content.SessionId);
            
            // Verify database state
            using var scope = _fixture.Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            // Ensure content.SessionId is not null before using it
            var payment = await repo.GetPaymentByStripeSessionIdAsync(content.SessionId!); 
            Assert.NotNull(payment);
        }

        [Fact]
        public async Task CreateCheckoutSession_InvalidPriceId_ReturnsBadRequest()
        {
            // Arrange
            var request = new SubscriptionRequest { PriceId = _invalidPriceId };

            // Act
            var response = await _client.PostAsJsonAsync("/api/subscription/create-checkout-session", request);
            
            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(CreateCheckoutSession_InvalidPriceId_ReturnsBadRequest)} - Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(CreateCheckoutSession_InvalidPriceId_ReturnsBadRequest)} - Response Body: {responseBody}");
            // *** END LOGGING ***

            // Assert
            // Now that authentication should be fixed, we expect BadRequest
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        private async Task SeedTestDataAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ecommerceContext>();
            
            if (!context.SubscriptionPlans.Any(p => p.Name == "Test Plan"))
            {
                context.SubscriptionPlans.Add(new SubscriptionPlan
                {
                    Id = Guid.NewGuid(),
                    Name = "Test Plan",
                    Price = 9.99m,
                    Description = "Test subscription plan"
                });
                await context.SaveChangesAsync();
            }
        }
    }

    [Collection("Integration Tests")]
    public class StripeWebhookControllerTests : IAsyncLifetime
    {
        private readonly HttpClient _client; 
        private readonly IntegrationTestFixture _fixture;
        private string _testSessionId = string.Empty;
        private readonly string _testUserId = "webhook_test_user"; 

        // DTO for parsing token response in CreateTestCheckoutSession
        private class TestTokenResponseDtoForWebhook
        {
            public string? Token { get; set; }
        }


        public StripeWebhookControllerTests(IntegrationTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.Factory.CreateClient(); 
        }

        public async Task InitializeAsync()
        {
            await _fixture.ResetDatabaseAsync();
            _testSessionId = await CreateTestCheckoutSession();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task HandleWebhook_ValidCheckoutSession_UpdatesSubscription()
        {
            // Arrange
            var jsonEvent = BuildCompletedSessionEvent();
            var signature = GenerateEventSignature(jsonEvent);

            // Act
            _client.DefaultRequestHeaders.Clear(); 
            _client.DefaultRequestHeaders.Add("Stripe-Signature", signature);
            var response = await _client.PostAsync("/api/stripewebhook",
                new StringContent(jsonEvent, Encoding.UTF8, "application/json"));

            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(HandleWebhook_ValidCheckoutSession_UpdatesSubscription)} - Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(HandleWebhook_ValidCheckoutSession_UpdatesSubscription)} - Response Body: {responseBody}");
            // *** END LOGGING ***

            // Assert
            response.EnsureSuccessStatusCode();
            var subscription = await GetUserSubscription();
            Assert.NotNull(subscription);
            Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        }

        [Fact]
        public async Task HandleWebhook_InvalidSignature_ReturnsBadRequest()
        {
            // Arrange
            var invalidSignature = "invalid_signature_test"; 
            var jsonEvent = BuildCompletedSessionEvent();

            // Act
            _client.DefaultRequestHeaders.Clear(); 
            _client.DefaultRequestHeaders.Add("Stripe-Signature", invalidSignature);
            var response = await _client.PostAsync("/api/stripewebhook",
                new StringContent(jsonEvent, Encoding.UTF8, "application/json"));

            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(HandleWebhook_InvalidSignature_ReturnsBadRequest)} - Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(HandleWebhook_InvalidSignature_ReturnsBadRequest)} - Response Body: {responseBody}");
            // *** END LOGGING ***

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        private async Task<string> CreateTestCheckoutSession()
        {
            // Step 1: Get a token for _testUserId
            var plainClient = _fixture.Factory.CreateClient(); // Use a plain client to get the token
            var tokenResponseMsg = await plainClient.GetAsync($"/api/test-auth/generate-test-token?userId={Uri.EscapeDataString(_testUserId)}&userName={Uri.EscapeDataString(_testUserId)}");
            
            Console.WriteLine($"DEBUG - Test: {nameof(CreateTestCheckoutSession)} - Token Gen Status Code: {tokenResponseMsg.StatusCode}");
            var tokenResponseBody = await tokenResponseMsg.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(CreateTestCheckoutSession)} - Token Gen Response Body: {tokenResponseBody}");
            tokenResponseMsg.EnsureSuccessStatusCode();

            var tokenData = JObject.Parse(tokenResponseBody);
            var actualToken = tokenData["token"]?.ToString();

            if (string.IsNullOrEmpty(actualToken))
            {
                throw new InvalidOperationException($"Failed to retrieve a valid token for test user '{_testUserId}' in CreateTestCheckoutSession. Response: {tokenResponseBody}");
            }

            // Step 2: Create an authorized client with the obtained token
            var authorizedClient = _fixture.Factory.CreateClient();
            authorizedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actualToken);
            
            var request = new SubscriptionRequest { PriceId = "price_webhook_test" }; 
            
            var response = await authorizedClient.PostAsJsonAsync("/api/subscription/create-checkout-session", request);

            // *** ADDED LOGGING ***
            Console.WriteLine($"DEBUG - Test: {nameof(CreateTestCheckoutSession)} - Create Session Status Code: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG - Test: {nameof(CreateTestCheckoutSession)} - Create Session Response Body: {responseBody}");
            // *** END LOGGING ***

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to create test checkout session. Status: {response.StatusCode}, Body: {responseBody}");
            }
            
            var content = await response.Content.ReadFromJsonAsync<CreateCheckoutSessionResponseDto>();
            if (string.IsNullOrEmpty(content?.SessionId)) // Check for null or empty
            {
                throw new InvalidOperationException($"CreateTestCheckoutSession returned null or empty SessionId. Response: {responseBody}");
            }
            return content.SessionId;
        }

        private string BuildCompletedSessionEvent() => $@"{{
            ""id"": ""evt_test_webhook_123"",
            ""object"": ""event"",
            ""type"": ""checkout.session.completed"",
            ""data"": {{
                ""object"": {{
                    ""id"": ""{_testSessionId}"",
                    ""client_reference_id"": ""{_testUserId}"", 
                    ""subscription"": ""sub_test_webhook_123"",
                    ""payment_status"": ""paid"",
                    ""metadata"": {{
                        ""user_id"": ""{_testUserId}"" 
                    }}
                }}
            }}
        }}";

        private string GenerateEventSignature(string jsonEvent)
        {
            var secret = _fixture.Configuration["Stripe:WebhookSecret"];
            if (string.IsNullOrEmpty(secret))
            {
                Console.WriteLine("DEBUG - Stripe:WebhookSecret is missing or empty in configuration for signature generation.");
                return "dummy_signature_due_to_missing_secret";
            }
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payloadToSign = $"{timestamp}.{jsonEvent}";
            return $"t={timestamp},v1={CryptoHelper.ComputeHMACSHA256(secret, payloadToSign)}";
        }

        private async Task<ecommerce.Db.Subscription?> GetUserSubscription()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            return await repo.GetSubscriptionByUserIdAsync(_testUserId);
        }
    }
}
*/