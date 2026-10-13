namespace JensenOnline.Api.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";

    // Hemlig nyckel – i produktion från secrets management (t.ex. Azure Key Vault / miljövariabel), aldrig i git.
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class SecurityOptions
{
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int GlobalPermitPerMinute { get; set; } = 200;
    public int AuthPermitPerMinute { get; set; } = 10;
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}
