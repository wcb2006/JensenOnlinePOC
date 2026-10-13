using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace JensenOnline.Api.Tests;

/// <summary>Startar hela API:t i minnet mot en egen, tillfällig SQLite-databas.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@jensenonline.se";
    public const string CustomerEmail = "kund@jensenonline.se";
    public const string Customer2Email = "kund2@jensenonline.se";
    public const string AdminPassword = "Test#AdminPassword1";
    public const string CustomerPassword = "Test#CustomerPassword1";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"jensenonline-test-{Guid.NewGuid():N}.db");

    protected virtual int AuthPermitPerMinute => 10_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-at-least-32-bytes-long!");
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:CustomerPassword", CustomerPassword);
        builder.UseSetting("Security:GlobalPermitPerMinute", "100000");
        builder.UseSetting("Security:AuthPermitPerMinute", AuthPermitPerMinute.ToString());
    }

    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public sealed class StrictRateLimitApiFactory : ApiFactory
{
    protected override int AuthPermitPerMinute => 3;
}

public sealed record LoginResult(string AccessToken, string RefreshCookie, int UserId);

public static class TestHelpers
{
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "JensenOnline";

    public static async Task<LoginResult> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new LoginResult(
            body.GetProperty("accessToken").GetString()!,
            GetRefreshCookie(response) ?? throw new InvalidOperationException("Ingen refresh-cookie."),
            body.GetProperty("user").GetProperty("id").GetInt32());
    }

    public static async Task<(string Email, string Password)> RegisterNewCustomerAsync(this HttpClient client)
    {
        var email = $"test-{Guid.NewGuid():N}@example.com";
        const string password = "Sakert#Losenord2026";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (email, password);
    }

    public static string? GetRefreshCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("jo_refresh=") && v.Length > "jo_refresh=".Length)
            : null;

    public static Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpMethod method, string url,
        string? token = null, object? body = null, string? cookie = null, bool csrfHeader = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrfHeader) request.Headers.Add(CsrfHeader, CsrfValue);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
