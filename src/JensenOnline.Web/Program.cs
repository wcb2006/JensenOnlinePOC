using System.Text.Json;

// Minimal värd för Vanilla JS-frontenden. Den körs på ett eget origin (https://localhost:7002)
// så att CORS-policyn i API:t faktiskt används, och den sätter en strikt Content Security Policy.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

var apiBaseUrl = (app.Configuration["ApiBaseUrl"] ?? "https://localhost:7001").TrimEnd('/');

// CSP: bara våra egna skript och stilmallar får köras, inga inline-skript, och JS får bara
// anropa vårt eget API. Även om en XSS-payload skulle hamna i sidan kan den inte köras eller skicka data vidare.
var contentSecurityPolicy = string.Join("; ",
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self'",
    "img-src 'self' data:",
    $"connect-src 'self' {apiBaseUrl}",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'",
    "frame-ancestors 'none'",
    "require-trusted-types-for 'script'");  // Blockerar farliga DOM-sinks som innerHTML i webbläsare som stöder Trusted Types

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = contentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers.CacheControl = "no-cache";
        return Task.CompletedTask;
    });
    await next();
});

// API-adressen hämtas från konfiguration så att den bara finns på ett ställe.
app.MapGet("/js/config.js", () =>
    Results.Text($"export const API_BASE = {JsonSerializer.Serialize(apiBaseUrl)};\n", "text/javascript"));

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
