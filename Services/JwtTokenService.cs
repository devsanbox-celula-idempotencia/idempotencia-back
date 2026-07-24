using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace idempotencia.Services;

/// <summary>
/// Implementación de <see cref="IJwtTokenService"/>. Firma JWT con los claims
/// UserId, Email y Role. La emisión del token no es "regla de negocio" de
/// dominio: es responsabilidad de infraestructura de autenticación.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings) => _settings = settings.Value;

    public AuthResponse CreateToken(UserIdentity user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.ExpirationMinutes);

        // Claims mínimos para autorización aguas abajo.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtClaimNames.UserId, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return new AuthResponse
        {
            Token = tokenString,
            ExpiresAt = expiresAt,
            UserId = user.UserId,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role
        };
    }
}
