using JensenOnline.Api.Data;
using JensenOnline.Api.Dtos;
using JensenOnline.Api.Models;
using JensenOnline.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JensenOnline.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AppDbContext db,
    TokenService tokens,
    IPasswordHasher<AppUser> hasher,
    AuditService audit,
    IOptions<JwtOptions> jwtOptions,
    IOptions<SecurityOptions> securityOptions) : ControllerBase
{
    public const string RefreshCookieName = "jo_refresh";
    public const string CsrfHeaderName = "X-Requested-With";
    public const string CsrfHeaderValue = "JensenOnline";
    private const string RefreshCookiePath = "/api/auth";

    // Hash som används när e-posten inte finns, så att svarstiden blir densamma (motverkar user enumeration via timing).
    private static readonly string DummyPasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), "dummy-password-for-timing");

    [AllowAnonymous]
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<UserDto>> Register(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);

        foreach (var error in PasswordPolicy.Validate(request.Password, email))
            ModelState.AddModelError(nameof(request.Password), error);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            await audit.LogAsync("RegisterFailed", false, email: email, details: "E-postadressen finns redan.");
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Kontot kunde inte skapas.",
                detail: "Kontrollera uppgifterna, eller logga in om du redan har ett konto.");
        }

        // Rollen sätts ALLTID av servern – en kund kan aldrig registrera sig som admin.
        var user = new AppUser { Email = email, Role = Roles.Customer };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await audit.LogAsync("Register", true, user.Id, user.Email);
        return StatusCode(StatusCodes.Status201Created, UserDto.From(user));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        var now = DateTime.UtcNow;
        var security = securityOptions.Value;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            hasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, request.Password);
            await audit.LogAsync("LoginFailed", false, email: email, details: "Okänd e-postadress.");
            return InvalidCredentials();
        }

        if (user.LockoutEndUtc > now)
        {
            await audit.LogAsync("LoginBlockedLockedOut", false, user.Id, user.Email);
            return Problem(statusCode: StatusCodes.Status423Locked, title: "Kontot är tillfälligt låst.",
                detail: "För många misslyckade inloggningsförsök. Försök igen senare.");
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed || user.IsDisabled)
        {
            if (result == PasswordVerificationResult.Failed)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= security.MaxFailedLogins)
                {
                    user.LockoutEndUtc = now.AddMinutes(security.LockoutMinutes);
                    user.FailedLoginCount = 0;
                    await db.SaveChangesAsync();
                    await audit.LogAsync("AccountLockedOut", false, user.Id, user.Email,
                        $"Låst i {security.LockoutMinutes} min efter {security.MaxFailedLogins} misslyckade försök.");
                    return InvalidCredentials();
                }
                await db.SaveChangesAsync();
            }

            await audit.LogAsync("LoginFailed", false, user.Id, user.Email, user.IsDisabled ? "Kontot är spärrat." : "Fel lösenord.");
            return InvalidCredentials();
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password);

        var response = await IssueTokensAsync(user);
        await audit.LogAsync("LoginSuccess", true, user.Id, user.Email);
        return response;
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh()
    {
        if (!HasCsrfHeader()) return CsrfRejected();

        var rawToken = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(rawToken)) return SessionInvalid();

        var hash = TokenService.HashToken(rawToken);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (stored?.User is null) return SessionInvalid();

        var user = stored.User;
        if (stored.RevokedAtUtc is not null)
        {
            // En redan använd (roterad) token skickas igen => den har troligen stulits.
            // Spärra alla användarens sessioner och ogiltigförklara access tokens.
            await RevokeAllRefreshTokensAsync(user.Id);
            user.RotateSecurityStamp();
            await db.SaveChangesAsync();
            await audit.LogAsync("RefreshTokenReuseDetected", false, user.Id, user.Email, "Alla sessioner har spärrats.");
            return SessionInvalid();
        }

        if (stored.ExpiresAtUtc <= DateTime.UtcNow || user.IsDisabled) return SessionInvalid();

        return await IssueTokensAsync(user, replacing: stored);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (!HasCsrfHeader()) return CsrfRejected();

        var rawToken = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(rawToken))
        {
            var hash = TokenService.HashToken(rawToken);
            var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored is not null && stored.RevokedAtUtc is null)
            {
                stored.RevokedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await audit.LogAsync("Logout", true, stored.UserId, stored.User?.Email);
            }
        }

        DeleteRefreshCookie();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == User.GetUserId());
        return user is null ? NotFound() : UserDto.From(user);
    }

    private async Task<AuthResponse> IssueTokensAsync(AppUser user, RefreshToken? replacing = null)
    {
        var now = DateTime.UtcNow;
        var (accessToken, accessExpires) = tokens.CreateAccessToken(user);
        var rawRefresh = TokenService.GenerateRefreshToken();
        var refreshHash = TokenService.HashToken(rawRefresh);
        var refreshExpires = now.AddDays(jwtOptions.Value.RefreshTokenDays);

        // Rotation: varje refresh token kan bara användas en gång.
        if (replacing is not null)
        {
            replacing.RevokedAtUtc = now;
            replacing.ReplacedByTokenHash = refreshHash;
        }

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();

        // HttpOnly: JavaScript (och därmed XSS) kan inte läsa refresh token.
        // Secure: skickas bara över HTTPS. SameSite=Strict: skickas inte vid anrop från andra sajter (CSRF).
        Response.Cookies.Append(RefreshCookieName, rawRefresh, RefreshCookieOptions(refreshExpires));

        return new AuthResponse(accessToken, accessExpires, UserDto.From(user));
    }

    private async Task RevokeAllRefreshTokensAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null).ToListAsync();
        foreach (var token in active) token.RevokedAtUtc = now;
    }

    private static CookieOptions RefreshCookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookieName, RefreshCookieOptions(null));

    // En egen header kan inte skickas cross-origin utan godkänd CORS-preflight => skydd mot CSRF på cookie-endpoints.
    private bool HasCsrfHeader() => Request.Headers[CsrfHeaderName] == CsrfHeaderValue;

    private ObjectResult CsrfRejected() =>
        Problem(statusCode: StatusCodes.Status403Forbidden, title: "Förfrågan avvisades.", detail: "Obligatorisk säkerhetsheader saknas.");

    private ObjectResult SessionInvalid()
    {
        DeleteRefreshCookie();
        return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sessionen är inte giltig.", detail: "Logga in igen.");
    }

    // Samma svar oavsett om e-posten finns, lösenordet är fel eller kontot är spärrat (motverkar user enumeration).
    private ObjectResult InvalidCredentials() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Inloggningen misslyckades.", detail: "Felaktig e-postadress eller lösenord.");

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
