using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Services;

/// <summary>
/// Implementación de <see cref="IAuthService"/>. Orquesta autenticación:
/// hashea/verifica contraseñas con BCrypt (SQL no valida el hash), invoca los
/// SPs vía repositorio y firma el JWT. No implementa reglas de negocio de
/// dominio: crear/vincular usuarios es responsabilidad de los SPs.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IJwtTokenService _jwt;

    public AuthService(IUserRepository users, IJwtTokenService jwt)
    {
        _users = users;
        _jwt = jwt;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        // El hash se calcula en el backend; el SP solo persiste el valor recibido.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var identity = await _users.RegisterUserAsync(
            request.Email, passwordHash, request.FullName, ct);

        return _jwt.CreateToken(identity);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var login = await _users.GetLoginByEmailAsync(request.Email, ct);

        // Mensaje genérico para no revelar si el correo existe.
        if (login is null)
            throw new AuthException("Credenciales inválidas.");

        // Usuario creado solo por OAuth: no tiene contraseña local.
        if (string.IsNullOrEmpty(login.PasswordHash))
            throw new AuthException("Esta cuenta usa inicio de sesión externo.");

        // La verificación del hash ocurre en el backend, nunca en SQL.
        if (!BCrypt.Net.BCrypt.Verify(request.Password, login.PasswordHash))
            throw new AuthException("Credenciales inválidas.");

        if (!login.IsActive)
            throw new AuthException("La cuenta está inactiva.");

        var identity = new UserIdentity
        {
            UserId = login.UserId,
            Email = login.Email,
            FullName = login.FullName,
            Role = login.Role
        };

        return _jwt.CreateToken(identity);
    }

    public async Task<AuthResponse> ExternalLoginAsync(
        string provider, string providerUserId, string email, string fullName,
        CancellationToken ct = default)
    {
        // El SP resuelve/crea/vincula el usuario OAuth y devuelve su identidad.
        var identity = await _users.UpsertExternalLoginAsync(
            provider, providerUserId, email, fullName, ct);

        return _jwt.CreateToken(identity);
    }
}
