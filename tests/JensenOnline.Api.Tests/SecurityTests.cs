using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace JensenOnline.Api.Tests;

/// <summary>
/// Säkerhetstester: varje test är ett attackscenario mot API:t och verifierar att
/// skyddet inte går att kringgå.
/// </summary>
public class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateApiClient();

    // ---------- 1. Authentication / Authorization ----------

    [Fact]
    public async Task Anonymous_user_cannot_access_protected_endpoints()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/orders/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/products", new { })).StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_use_admin_functions()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);

        var product = new { name = "Hack", category = "Test", price = 1m, stock = 1, description = "" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, "/api/admin/users", customer.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, "/api/admin/orders", customer.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Post, "/api/products", customer.AccessToken, product)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Delete, "/api/products/1", customer.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_escalate_own_role()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var response = await _client.SendAsync(HttpMethod.Put, $"/api/admin/users/{customer.UserId}/role", customer.AccessToken, new { role = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_can_use_admin_functions()
    {
        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, "/api/admin/users", admin.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, "/api/admin/audit-logs", admin.AccessToken)).StatusCode);
    }

    // ---------- 2. Manipulerade tokens ----------

    [Fact]
    public async Task Token_with_modified_role_claim_is_rejected()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var parts = customer.AccessToken.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        var forgedPayload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.Replace("\"Customer\"", "\"Admin\"")));
        var forgedToken = $"{parts[0]}.{forgedPayload}.{parts[2]}";

        var response = await _client.SendAsync(HttpMethod.Get, "/api/admin/users", forgedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsigned_token_with_alg_none_is_rejected()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var payload = customer.AccessToken.Split('.')[1];
        var header = Base64Url.EncodeToString("""{"alg":"none","typ":"JWT"}"""u8);
        var response = await _client.SendAsync(HttpMethod.Get, "/api/orders/mine", $"{header}.{payload}.");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_users_existing_token_stops_working_immediately()
    {
        var (email, password) = await _client.RegisterNewCustomerAsync();
        var victim = await _client.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, "/api/orders/mine", victim.AccessToken)).StatusCode);

        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var disable = await _client.SendAsync(HttpMethod.Put, $"/api/admin/users/{victim.UserId}/status", admin.AccessToken, new { isDisabled = true });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Get, "/api/orders/mine", victim.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: victim.RefreshCookie)).StatusCode);
    }

    // ---------- 3. IDOR – manipulera ID i URL ----------

    [Fact]
    public async Task Customer_cannot_read_another_customers_order_by_changing_id()
    {
        var owner = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var create = await _client.SendAsync(HttpMethod.Post, "/api/orders", owner.AccessToken,
            new { items = new[] { new { productId = 3, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var orderId = (await create.JsonAsync()).GetProperty("id").GetInt32();

        var attacker = await _client.LoginAsync(ApiFactory.Customer2Email, ApiFactory.CustomerPassword);
        var attack = await _client.SendAsync(HttpMethod.Get, $"/api/orders/{orderId}", attacker.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, attack.StatusCode);

        var mine = await (await _client.SendAsync(HttpMethod.Get, "/api/orders/mine", attacker.AccessToken)).JsonAsync();
        Assert.DoesNotContain(mine.EnumerateArray(), o => o.GetProperty("id").GetInt32() == orderId);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, $"/api/orders/{orderId}", owner.AccessToken)).StatusCode);
        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, $"/api/orders/{orderId}", admin.AccessToken)).StatusCode);
    }

    // ---------- 4. Ogiltiga data / mass assignment ----------

    [Fact]
    public async Task Invalid_product_data_is_rejected()
    {
        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        object[] invalidProducts =
        [
            new { name = "", category = "Test", price = 10m, stock = 1, description = "" },                          // tomt namn
            new { name = "Skärm", category = "Test", price = -5m, stock = 1, description = "" },                    // negativt pris
            new { name = "Skärm", category = "Test", price = 10m, stock = -1, description = "" },                   // negativt lager
            new { name = new string('A', 101), category = "Test", price = 10m, stock = 1, description = "" },       // för långt
            new { name = "<script>alert(1)</script>", category = "Test", price = 10m, stock = 1, description = "" }, // otillåtna tecken
            new { name = "Skärm", category = "Test", price = "gratis", stock = 1, description = "" },               // fel datatyp
        ];

        foreach (var product in invalidProducts)
        {
            var response = await _client.SendAsync(HttpMethod.Post, "/api/products", admin.AccessToken, product);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Registration_cannot_set_role_mass_assignment()
    {
        var email = $"attacker-{Guid.NewGuid():N}@example.com";
        var response = await _client.PostAsJsonAsync("/api/auth/register", new { email, password = "Sakert#Losenord2026", role = "Admin" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = "Sakert#Losenord2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Client_cannot_set_order_price()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var manipulated = await _client.SendAsync(HttpMethod.Post, "/api/orders", customer.AccessToken,
            new { items = new[] { new { productId = 1, quantity = 1, unitPrice = 0.01m } } });
        Assert.Equal(HttpStatusCode.BadRequest, manipulated.StatusCode);

        var product = await (await _client.GetAsync("/api/products/4")).JsonAsync();
        var created = await _client.SendAsync(HttpMethod.Post, "/api/orders", customer.AccessToken,
            new { items = new[] { new { productId = 4, quantity = 2 } } });
        var order = await created.JsonAsync();
        Assert.Equal(product.GetProperty("price").GetDecimal() * 2, order.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Order_quantity_limits_are_enforced()
    {
        var customer = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        foreach (var quantity in new[] { 0, -1, 11, 1_000_000 })
        {
            var response = await _client.SendAsync(HttpMethod.Post, "/api/orders", customer.AccessToken,
                new { items = new[] { new { productId = 1, quantity } } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Weak_passwords_are_rejected()
    {
        foreach (var password in new[] { "kort", "allasmåbokstäver123!", "INGENGEMEN123!", "IngaSiffror!!!", "IngetSpecial1234", "Password123!" })
        {
            var response = await _client.PostAsJsonAsync("/api/auth/register", new { email = $"weak-{Guid.NewGuid():N}@example.com", password });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // ---------- 5. XSS ----------

    [Fact]
    public async Task Xss_payload_is_stored_as_plain_text_and_served_as_json()
    {
        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        const string payload = "<img src=x onerror=alert('xss')><script>alert(1)</script>";
        var create = await _client.SendAsync(HttpMethod.Post, "/api/products", admin.AccessToken,
            new { name = "XSS-test", category = "Test", price = 1m, stock = 1, description = payload });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.JsonAsync()).GetProperty("id").GetInt32();

        var response = await _client.GetAsync($"/api/products/{id}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(payload, (await response.JsonAsync()).GetProperty("description").GetString());
    }

    // ---------- 6. SQL injection ----------

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE Products; --")]
    [InlineData("\" OR \"\"=\"")]
    [InlineData("%' UNION SELECT Email, PasswordHash FROM Users --")]
    public async Task Sql_injection_in_search_is_treated_as_text(string injection)
    {
        var response = await _client.GetAsync($"/api/products?search={Uri.EscapeDataString(injection)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.JsonAsync()).GetArrayLength());

        var all = await (await _client.GetAsync("/api/products")).JsonAsync();
        Assert.True(all.GetArrayLength() >= 8, "Produkttabellen ska vara intakt.");
    }

    // ---------- 7. Brute force / account lockout / user enumeration ----------

    [Fact]
    public async Task Account_is_locked_after_five_failed_logins_even_with_correct_password()
    {
        var (email, password) = await _client.RegisterNewCustomerAsync();
        for (var i = 0; i < 5; i++)
        {
            var failed = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = "FelLosenord#123" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var correct = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Locked, correct.StatusCode);
    }

    [Fact]
    public async Task Login_error_does_not_reveal_whether_account_exists()
    {
        var existing = await _client.PostAsJsonAsync("/api/auth/login", new { email = ApiFactory.Customer2Email, password = "FelLosenord#123" });
        var missing = await _client.PostAsJsonAsync("/api/auth/login", new { email = "finns-inte@example.com", password = "FelLosenord#123" });

        Assert.Equal(existing.StatusCode, missing.StatusCode);
        var a = await existing.JsonAsync();
        var b = await missing.JsonAsync();
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
    }

    // ---------- 8. Refresh tokens / stulna tokens / CSRF ----------

    [Fact]
    public async Task Refresh_token_is_rotated_and_reuse_revokes_all_sessions()
    {
        var (email, password) = await _client.RegisterNewCustomerAsync();
        var login = await _client.LoginAsync(email, password);

        var first = await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: login.RefreshCookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotatedCookie = TestHelpers.GetRefreshCookie(first);
        Assert.NotNull(rotatedCookie);
        Assert.NotEqual(login.RefreshCookie, rotatedCookie);
        var newAccessToken = (await first.JsonAsync()).GetProperty("accessToken").GetString();

        // Angriparen återanvänder den gamla (stulna) refresh token.
        var reuse = await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: login.RefreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // Hela sessionen spärras – även den nya refresh token och access token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: rotatedCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Get, "/api/orders/mine", newAccessToken)).StatusCode);
    }

    [Fact]
    public async Task Refresh_without_csrf_header_is_rejected()
    {
        var (email, password) = await _client.RegisterNewCustomerAsync();
        var login = await _client.LoginAsync(email, password);
        var response = await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: login.RefreshCookie, csrfHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_cookie_has_secure_attributes()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = ApiFactory.Customer2Email, password = ApiFactory.CustomerPassword });
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("jo_refresh=")).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var (email, password) = await _client.RegisterNewCustomerAsync();
        var login = await _client.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Post, "/api/auth/logout", cookie: login.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Post, "/api/auth/refresh", cookie: login.RefreshCookie)).StatusCode);
    }

    // ---------- 9. CORS och felhantering ----------

    [Fact]
    public async Task Cors_only_allows_the_frontend_origin()
    {
        async Task<HttpResponseMessage> Preflight(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/orders");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            return await _client.SendAsync(request);
        }

        var allowed = await Preflight("https://localhost:7002");
        Assert.Equal("https://localhost:7002", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var evil = await Preflight("https://evil.example.com");
        Assert.False(evil.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Error_responses_do_not_leak_internal_details()
    {
        var response = await _client.PostAsync("/api/auth/login", new StringContent("{ ogiltig json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("   at ", body);           // ingen stacktrace
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Idor_attempt_is_written_to_audit_log()
    {
        var owner = await _client.LoginAsync(ApiFactory.CustomerEmail, ApiFactory.CustomerPassword);
        var create = await _client.SendAsync(HttpMethod.Post, "/api/orders", owner.AccessToken,
            new { items = new[] { new { productId = 2, quantity = 1 } } });
        var orderId = (await create.JsonAsync()).GetProperty("id").GetInt32();

        var attacker = await _client.LoginAsync(ApiFactory.Customer2Email, ApiFactory.CustomerPassword);
        await _client.SendAsync(HttpMethod.Get, $"/api/orders/{orderId}", attacker.AccessToken);

        var admin = await _client.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var logs = await (await _client.SendAsync(HttpMethod.Get, "/api/admin/audit-logs?take=500", admin.AccessToken)).JsonAsync();
        Assert.Contains(logs.EnumerateArray(), l =>
            l.GetProperty("action").GetString() == "OrderAccessDenied" &&
            l.GetProperty("details").GetString()!.Contains($"order {orderId} "));
    }
}

/// <summary>Rate limiting testas med en egen instans med låg gräns.</summary>
public class RateLimitTests(StrictRateLimitApiFactory factory) : IClassFixture<StrictRateLimitApiFactory>
{
    [Fact]
    public async Task Login_endpoint_is_rate_limited()
    {
        var client = factory.CreateApiClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "brute@example.com", password = $"Gissning#{i}xxxxx" });
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(3), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(3), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }
}
