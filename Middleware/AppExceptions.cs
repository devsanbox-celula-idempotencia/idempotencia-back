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

/// <summary>
/// Recurso inexistente O que no pertenece al usuario autenticado (404). Se
/// usa el mismo mensaje/código para ambos casos a propósito — no hay que
/// revelarle a un usuario si el <c>databaseId</c> que probó existe pero es de
/// otra persona (evita enumeración de IDs ajenos).
/// </summary>
public class NotFoundException : AppException
{
    public NotFoundException(string message)
        : base(message, StatusCodes.Status404NotFound)
    {
    }
}
