using Microsoft.Data.SqlClient;

namespace idempotencia.Middleware;

/// <summary>
/// Traduce una excepción al par (código HTTP, mensaje) que ve el cliente.
/// Fuente única de esta lógica, compartida por el middleware de errores y por
/// el callback OAuth (que la usa para armar la URL de error del frontend).
/// </summary>
public static class ApiExceptionMapper
{
    public static (int StatusCode, string Message) Map(Exception exception) => exception switch
    {
        AppException app => (app.StatusCode, app.Message),

        // THROW/RAISERROR intencional de un SP (regla de negocio) => 400.
        SqlException sql when sql.Number >= 50000 => (StatusCodes.Status400BadRequest, sql.Message),

        // Cualquier otro error de SQL Server (SP inexistente, conexión, etc.).
        SqlException => (StatusCodes.Status500InternalServerError, "Ocurrió un error al procesar la solicitud."),

        _ => (StatusCodes.Status500InternalServerError, "Ocurrió un error inesperado.")
    };
}
