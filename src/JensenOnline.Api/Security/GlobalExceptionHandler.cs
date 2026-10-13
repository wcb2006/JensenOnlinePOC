using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace JensenOnline.Api.Security;

/// <summary>
/// Central felhantering: detaljer (stacktrace, SQL, sökvägar) loggas internt men klienten får
/// bara ett generiskt meddelande och ett trace-id som kan kopplas till loggen.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Ohanterat fel på {Method} {Path}. TraceId={TraceId}",
            context.Request.Method, context.Request.Path, context.TraceIdentifier);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Ett oväntat fel inträffade.",
                Detail = "Försök igen senare. Ange trace-id om du kontaktar supporten.",
            },
        });
    }
}
