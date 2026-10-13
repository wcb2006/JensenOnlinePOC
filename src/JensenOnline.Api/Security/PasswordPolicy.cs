namespace JensenOnline.Api.Security;

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    // I produktion: kontrollera mot en stor lista över läckta lösenord (t.ex. Have I Been Pwned k-anonymity-API).
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password123!", "Password1234", "Qwerty123456!", "Welcome12345!", "Sommar2025!!", "Sommar2026!!",
        "Vinter2026!!", "Losenord123!", "Lösenord123!", "Admin1234567!", "Jensen123456!",
    };

    public static IReadOnlyList<string> Validate(string password, string email)
    {
        var errors = new List<string>();

        if (password.Length < MinLength) errors.Add($"Lösenordet måste vara minst {MinLength} tecken.");
        if (password.Length > MaxLength) errors.Add($"Lösenordet får vara högst {MaxLength} tecken.");
        if (!password.Any(char.IsUpper)) errors.Add("Lösenordet måste innehålla minst en versal.");
        if (!password.Any(char.IsLower)) errors.Add("Lösenordet måste innehålla minst en gemen.");
        if (!password.Any(char.IsDigit)) errors.Add("Lösenordet måste innehålla minst en siffra.");
        if (password.All(char.IsLetterOrDigit)) errors.Add("Lösenordet måste innehålla minst ett specialtecken.");

        var localPart = email.Split('@')[0];
        if (localPart.Length >= 3 && password.Contains(localPart, StringComparison.OrdinalIgnoreCase))
            errors.Add("Lösenordet får inte innehålla din e-postadress.");

        if (CommonPasswords.Contains(password)) errors.Add("Lösenordet är för vanligt.");

        return errors;
    }
}
