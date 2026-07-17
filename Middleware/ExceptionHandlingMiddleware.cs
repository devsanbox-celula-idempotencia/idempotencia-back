using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace idempotencia.Middleware;

/// <summary>
/// Middleware central de manejo de errores. Traduce excepciones a respuestas
/// JSON uniformes (ProblemDetails-like) y evita filtrar detalles internos.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            // Errores esperados del dominio/autenticación.
            await WriteProblemAsync(context, ex.StatusCode, ex.Message);
        }
        catch (SqlException ex)
        {
            // Errores lanzados por los Stored Procedures (THROW/RAISERROR).
            // Los errores >= 50000 suelen ser validaciones de negocio del SP.
            _logger.LogWarning(ex, "Error de SQL Server al ejecutar un SP.");
            var status = ex.Number >= 50000
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status500InternalServerError;
            var message = status == StatusCodes.Status400BadRequest
                ? ex.Message
                : "Ocurrió un error al procesar la solicitud.";
            await WriteProblemAsync(context, status, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error no controlado.");
            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Ocurrió un error inesperado.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new
        {
            status = statusCode,
            error = message
        });

        await context.Response.WriteAsync(payload);
    }
}
