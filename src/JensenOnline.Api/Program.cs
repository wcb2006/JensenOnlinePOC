using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using JensenOnline.Api.Controllers;
using JensenOnline.Api.Data;
using JensenOnline.Api.Models;
using JensenOnline.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;                 // Avslöja inte serverteknik i onödan
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;   // API:t tar bara emot små JSON-anrop – stora bodies avvisas (DoS)
});

// ---------- Konfiguration (valideras vid start – fail fast om t.ex. JWT-nyckeln saknas) ----------
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(o => Encoding.UTF8.GetByteCount(o.SigningKey) >= 32, "Jwt:SigningKey saknas eller är kortare än 32 byte.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience), "Jwt:Issuer och Jwt:Audience måste anges.")
    .ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().Bind(builder.Configuration.GetSection("Security"));

// ---------- Databas ----------
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

// ---------- Tjänster ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>(); // PBKDF2-HMAC-SHA512, 100 000 iterationer, unikt salt
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AuditService>();

// ---------- Authentication (JWT) ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.CreateSigningKey(jwt),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],   // Förhindrar "alg: none" och algoritmbyte
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "email",
            RoleClaimType = "role",
        };

        options.Events = new JwtBearerEvents
        {
            // Signaturen räcker inte: kontrollera också att kontot är aktivt och att rollen inte ändrats sedan token utfärdades.
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var sub = context.Principal?.FindFirst("sub")?.Value;
                var stamp = context.Principal?.FindFirst("sstamp")?.Value;

                if (!int.TryParse(sub, out var userId))
                {
                    context.Fail("Ogiltig token.");
                    return;
                }

                var user = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.SecurityStamp, u.IsDisabled })
                    .FirstOrDefaultAsync();

                if (user is null || user.IsDisabled || user.SecurityStamp != stamp)
                    context.Fail("Token är inte längre giltig.");
            },
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtAuth");
                logger.LogWarning("Ogiltig token från {Ip}: {Reason}",
                    context.HttpContext.Connection.RemoteIpAddress, context.Exception.GetType().Name);
                return Task.CompletedTask;
            },
            OnForbidden = async context =>
            {
                var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
                var user = context.HttpContext.User;
                await audit.LogAsync("AccessDenied", false,
                    int.TryParse(user.FindFirst("sub")?.Value, out var id) ? id : null,
                    user.Identity?.Name,
                    $"{context.Request.Method} {context.Request.Path}");
            },
        };
    });

// ---------- Authorization: "deny by default" ----------
// Alla endpoints kräver inloggning om de inte uttryckligen markeras [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---------- Rate limiting (skydd mot brute force, API abuse och överbelastning) ----------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.GlobalPermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    // Striktare gräns för inloggning/registrering.
    options.AddPolicy(RateLimitPolicies.Auth, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.AuthPermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            http.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();

        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting")
            .LogWarning("Rate limit överskriden: {Ip} {Method} {Path}", ClientIp(http), http.Request.Method, http.Request.Path);

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "För många förfrågningar.",
                Detail = "Vänta en stund och försök igen.",
            },
        });
    };
});

// ---------- CORS: endast vår egen frontend får anropa API:t från en webbläsare ----------
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .WithOrigins(allowedOrigins)
    .WithMethods("GET", "POST", "PUT", "DELETE")
    .WithHeaders("Content-Type", "Authorization", AuthController.CsrfHeaderName)
    .AllowCredentials()
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

// ---------- Controllers, validering och felhantering ----------
builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Okända fält i JSON (t.ex. "role": "Admin" eller "unitPrice") ger 400 i stället för att tyst ignoreras.
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    context.ProblemDetails.Extensions.Remove("exception");
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    DbSeeder.Seed(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>(), app.Configuration, app.Logger);
}

// ---------- Middleware-pipeline (ordningen spelar roll) ----------
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.Run();

// I produktion bakom proxy/API Gateway: använd ForwardedHeaders så att rätt klient-IP används.
static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

public partial class Program;
