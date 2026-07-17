namespace idempotencia.Middleware;

/// <summary>
/// Excepción base de la aplicación que transporta un código HTTP sugerido.
/// El middleware la traduce a una respuesta de error uniforme.
/// </summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    public AppException(string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>Credenciales inválidas o usuario inactivo (401).</summary>
public class AuthException : AppException
{
    public AuthException(string message)
        : base(message, StatusCodes.Status401Unauthorized)
    {
    }
}
