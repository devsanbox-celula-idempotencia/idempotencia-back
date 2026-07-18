using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace idempotencia.Middleware;

/// <summary>
/// Middleware central de manejo de errores. Registra la excepción y responde en
/// JSON uniforme, delegando el mapeo a <see cref="ApiExceptionMapper"/>.
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
        catch (Exception ex)
        {
            Log(ex);
            var (statusCode, message) = ApiExceptionMapper.Map(ex);
            await WriteProblemAsync(context, statusCode, message);
        }
    }

    private void Log(Exception ex)
    {
        switch (ex)
        {
            case AppException:
                break; // error esperado del dominio: no se registra como fallo
            case SqlException:
                _logger.LogWarning(ex, "Error de SQL Server al ejecutar un SP.");
                break;
            default:
                _logger.LogError(ex, "Error no controlado.");
                break;
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new { status = statusCode, error = message });
        await context.Response.WriteAsync(payload);
    }
}
