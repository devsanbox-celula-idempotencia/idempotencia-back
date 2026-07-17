using idempotencia.DTOs;
using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>Abstracción para la generación de JWT a partir de una identidad.</summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Firma un JWT con los claims UserId, Email y Role, y arma la respuesta
    /// de autenticación (token + expiración + datos del usuario).
    /// </summary>
    AuthResponse CreateToken(UserIdentity user);
}
