using System.Security.Claims;

namespace JensenOnline.Api.Security;

public static class ClaimsPrincipalExtensions
{
    // Användarens id tas ALLTID från den signerade token – aldrig från URL, query eller body.
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue("sub") ?? throw new InvalidOperationException("Token saknar sub-claim."));
}
