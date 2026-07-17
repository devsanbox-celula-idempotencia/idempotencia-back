using idempotencia.DTOs;

namespace idempotencia.Interfaces;

/// <summary>
/// Construye las URLs de retorno al frontend tras el callback OAuth: una con la
/// sesión (JWT + datos) y otra con el mensaje de error.
/// </summary>
public interface IOAuthRedirectBuilder
{
    string BuildSuccess(AuthResponse response);
    string BuildError(string message);
}
