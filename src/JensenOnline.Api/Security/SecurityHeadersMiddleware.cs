namespace JensenOnline.Api.Security;

/// <summary>
/// Lägger till säkerhetsheaders på alla svar. OnStarting används så att headers finns kvar
/// även när felhanteringen har rensat svaret.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";                            // Ingen MIME-sniffning (JSON tolkas aldrig som HTML/JS)
            headers["X-Frame-Options"] = "DENY";                                     // Clickjacking
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"; // API:t ska aldrig rendera innehåll
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers.CacheControl = "no-store";                                        // Känsliga svar (order, användare) cachas inte
            return Task.CompletedTask;
        });

        return next(context);
    }
}
