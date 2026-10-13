using JensenOnline.Api.Data;
using JensenOnline.Api.Models;

namespace JensenOnline.Api.Security;

/// <summary>
/// Säkerhets-/audit-loggning. Skriver både till databasen (spårbarhet, kan granskas av admin)
/// och till ILogger (kan skickas till extern logg-/övervakningstjänst/SIEM).
/// Lösenord och tokens loggas aldrig.
/// </summary>
public sealed class AuditService(AppDbContext db, IHttpContextAccessor httpContextAccessor, ILogger<AuditService> logger)
{
    public async Task LogAsync(string action, bool success, int? userId = null, string? email = null, string? details = null)
    {
        var ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        var entry = new AuditLog
        {
            Action = action,
            Success = success,
            UserId = userId,
            Email = Clean(email, 254),
            Details = Clean(details, 500),
            IpAddress = ip,
        };

        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync();

        logger.Log(success ? LogLevel.Information : LogLevel.Warning,
            "AUDIT {Action} Success={Success} UserId={UserId} Email={Email} Ip={Ip} Details={Details}",
            entry.Action, entry.Success, entry.UserId, entry.Email, entry.IpAddress, entry.Details);
    }

    // Tar bort kontrolltecken (t.ex. radbrytningar) så att användarstyrd text inte kan förfalska loggrader (log injection).
    private static string? Clean(string? value, int maxLength)
    {
        if (value is null) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
