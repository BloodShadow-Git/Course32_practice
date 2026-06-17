using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace IntegrationTests
{
    public class SystemTests
    {
        private readonly HttpClient _client;
        private readonly HttpClient _adminClient;

        public SystemTests()
        {
            _client = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
            _client.DefaultRequestHeaders.Add("X-Api-Key", "dev_super_secret_api_key_123");

            _adminClient = new HttpClient { BaseAddress = new Uri("http://localhost:8081") };
            _adminClient.DefaultRequestHeaders.Add("X-Api-Key", "dev_super_secret_api_key_123");
        }

        private async Task<AuthResult> RegisterAndLoginAsync(string username, string password, bool isAdmin = false)
        {
            var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password = password });
            // Don't EnsureSuccessStatusCode for registration, as user might already exist

            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
            loginResponse.EnsureSuccessStatusCode();
            
            var result = await loginResponse.Content.ReadFromJsonAsync<AuthResult>();
            result.Should().NotBeNull();
            
            return result!;
        }

        [Fact]
        public async Task Test1_RegisterAndLogin_Success()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var result = await RegisterAndLoginAsync(testUser, "password123");
            
            result.Token.Should().NotBeNullOrEmpty();
            result.Username.Should().Be(testUser);
            result.IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task Test2_ClickingIncreasesBalance()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);

            // Send 10 clicks
            var clickResponse = await _client.PostAsJsonAsync("/api/click", new { Clicks = 10 });
            clickResponse.EnsureSuccessStatusCode();

            // Wait for RabbitMQ message to be processed by PlayerService
            PlayerInfo? playerInfo = null;
            for (int i = 0; i < 50; i++) // poll up to 5 seconds
            {
                await Task.Delay(100);
                var balResponse = await _client.GetAsync($"/api/player/{auth.UserId}/balance");
                if (balResponse.IsSuccessStatusCode)
                {
                    playerInfo = await balResponse.Content.ReadFromJsonAsync<PlayerInfo>();
                    if (playerInfo?.Balance >= 10) break;
                }
            }

            playerInfo.Should().NotBeNull();
            playerInfo!.Balance.Should().BeGreaterThanOrEqualTo(10);
        }

        [Fact]
        public async Task Test3_StoreBuy_DecreasesBalance_And_UpdatesInventory()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);

            // Need 100 points for first item. Send 100 clicks.
            await _client.PostAsJsonAsync("/api/click", new { Clicks = 100 });
            
            PlayerInfo? playerInfo = null;
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                var balResponse = await _client.GetAsync($"/api/player/{auth.UserId}/balance");
                if (balResponse.IsSuccessStatusCode)
                {
                    playerInfo = await balResponse.Content.ReadFromJsonAsync<PlayerInfo>();
                    if (playerInfo?.Balance >= 100) break;
                }
            }
            playerInfo!.Balance.Should().BeGreaterThanOrEqualTo(100, "Should have 100 points before buying");

            // Buy Item 1
            var buyResponse = await _client.PostAsJsonAsync("/api/store/buy", new { ItemId = 1 });
            buyResponse.EnsureSuccessStatusCode();

            // Fetch balance again
            var finalBalResponse = await _client.GetAsync($"/api/player/{auth.UserId}/balance");
            var finalInfo = await finalBalResponse.Content.ReadFromJsonAsync<PlayerInfo>();

            finalInfo.Should().NotBeNull();
            finalInfo!.Balance.Should().Be(0); // 100 - 100 = 0
            finalInfo.Inventory.Should().HaveCount(1);
            finalInfo.Inventory[0].ItemName.Should().Be("Улучшенный курсор");
            finalInfo.Inventory[0].Count.Should().Be(1);
        }

        [Fact]
        public async Task Test4_AdminCanToggleApiKeys()
        {
            // 1. Create normal user and generate a key
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);
            
            var keyResponse = await _client.PostAsJsonAsync("/api/auth/generate-key", new { Name = "Test Key" });
            keyResponse.EnsureSuccessStatusCode();
            var keyResult = await keyResponse.Content.ReadFromJsonAsync<ApiKeyResult>();
            
            // 2. Login as admin (using predefined admin user injected by EF Core)
            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "superadmin_password123" });
            loginResponse.EnsureSuccessStatusCode();
            var adminAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResult>();
            adminAuth!.IsAdmin.Should().BeTrue();

            _adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminAuth.Token);

            // 3. Admin gets all keys
            var allKeysResponse = await _adminClient.GetAsync("/api/admin/keys");
            allKeysResponse.EnsureSuccessStatusCode();
            var allKeys = await allKeysResponse.Content.ReadFromJsonAsync<List<AdminApiKeyInfo>>();

            allKeys.Should().NotBeNull();
            allKeys.Should().Contain(k => k.Key == keyResult!.ApiKey && k.IsActive);

            var keyId = allKeys!.Find(k => k.Key == keyResult!.ApiKey)!.Id;

            // 4. Admin toggles the key
            var toggleResponse = await _adminClient.PostAsync($"/api/admin/keys/{keyId}/toggle", null);
            toggleResponse.EnsureSuccessStatusCode();

            // 5. Verify it's toggled
            var updatedKeysResponse = await _adminClient.GetFromJsonAsync<List<AdminApiKeyInfo>>("/api/admin/keys");
            var updatedKey = updatedKeysResponse!.Find(k => k.Id == keyId);
            
            updatedKey.Should().NotBeNull();
            updatedKey!.IsActive.Should().BeFalse();
            
            // Validate that the key returns 403 Forbidden because it is disabled
            var valResponse = await _client.PostAsJsonAsync("/api/auth/validate-key", new ValidateKeyRequest { ApiKey = keyResult!.ApiKey, Action = "test" });
            valResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
            var errBody = await valResponse.Content.ReadAsStringAsync();
            errBody.Should().Contain("Ключ отклонён");
        }

        [Fact]
        public async Task Test4b_InvalidKeyReturnsNotFound()
        {
            var valResponse = await _client.PostAsJsonAsync("/api/auth/validate-key", new ValidateKeyRequest { ApiKey = "fake_key_123", Action = "test" });
            valResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            var errBody = await valResponse.Content.ReadAsStringAsync();
            errBody.Should().Contain("Ключ незарегестрирован");
        }

        [Fact]
        public async Task Test5_RegisterFailsIfUsernameExists()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            await RegisterAndLoginAsync(testUser, "password123");
            var duplicateRegResponse = await _client.PostAsJsonAsync("/api/auth/register", new { Username = testUser, Password = "newpassword" });
            duplicateRegResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Test6_LoginFailsWithWrongPassword()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            await RegisterAndLoginAsync(testUser, "password123");
            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = testUser, Password = "wrongpassword" });
            loginResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Test7_LoginFailsIfUserDoesNotExist()
        {
            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = "nonexistentuser123", Password = "password" });
            loginResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Test8_StoreBuyFailsIfInsufficientFunds()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);

            var buyResponse = await _client.PostAsJsonAsync("/api/store/buy", new { ItemId = 1 });
            buyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Test9_StoreBuyFailsIfItemDoesNotExist()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);

            var buyResponse = await _client.PostAsJsonAsync("/api/store/buy", new { ItemId = 99999 });
            buyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Test10_AdminBlockUser_LoginFails()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");

            var adminAuth = await RegisterAndLoginAsync("admin", "superadmin_password123", isAdmin: true);
            _adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminAuth.Token);

            var toggleResponse = await _adminClient.PostAsync($"/api/admin/players/{auth.UserId}/toggle-block", null);
            toggleResponse.EnsureSuccessStatusCode();

            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = testUser, Password = "password123" });
            loginResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            var errStr = await loginResponse.Content.ReadAsStringAsync();
            errStr.Should().Contain("заблокирован");
        }

        [Fact]
        public async Task Test11_BlockedUser_KeysRejected()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);
            
            var keyResponse = await _client.PostAsJsonAsync("/api/auth/generate-key", new { Name = "Test Key" });
            keyResponse.EnsureSuccessStatusCode();

            var keyResult = await keyResponse.Content.ReadFromJsonAsync<ApiKeyResult>();

            var adminAuth = await RegisterAndLoginAsync("admin", "superadmin_password123", isAdmin: true);
            _adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminAuth.Token);

            var toggleResponse = await _adminClient.PostAsync($"/api/admin/players/{auth.UserId}/toggle-block", null);
            toggleResponse.EnsureSuccessStatusCode();

            // When a user is blocked, their keys are deactivated.
            // Check if key is deactivated by getting keys list from admin
            var allKeysResponse = await _adminClient.GetFromJsonAsync<List<AdminApiKeyInfo>>("/api/admin/keys");
            var keyStatus = allKeysResponse!.Find(k => k.Key == keyResult!.ApiKey);
            keyStatus.Should().NotBeNull();
            keyStatus!.IsActive.Should().BeFalse();
        }

        [Fact]
        public async Task Test12_AdminDeleteUser_Succeeds()
        {
            var testUser = $"test_{Guid.NewGuid():N}";
            var auth = await RegisterAndLoginAsync(testUser, "password123");

            var adminAuth = await RegisterAndLoginAsync("admin", "superadmin_password123", isAdmin: true);
            _adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminAuth.Token);

            var deleteResponse = await _adminClient.DeleteAsync($"/api/admin/players/{auth.UserId}");
            deleteResponse.EnsureSuccessStatusCode();

            var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { Username = testUser, Password = "password123" });
            loginResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        // DTOs
        public class AuthResult
        {
            public string Token { get; set; } = string.Empty;
            public int UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public bool IsAdmin { get; set; }
        }

        public class ApiKeyResult
        {
            public string ApiKey { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        public class PlayerInfo
        {
            public int Balance { get; set; }
            public List<PlayerItem> Inventory { get; set; } = new();
        }

        public class PlayerItem
        {
            public string ItemName { get; set; } = string.Empty;
            public int Count { get; set; }
        }

        public class AdminApiKeyInfo
        {
            public int Id { get; set; }
            public string PlayerName { get; set; } = string.Empty;
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int CallsCount { get; set; }
            public bool IsActive { get; set; }
        }

        public class ValidateKeyRequest
        {
            public string ApiKey { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
        }
    }
}