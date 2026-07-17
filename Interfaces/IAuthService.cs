using idempotencia.DTOs;
using idempotencia.Models;

namespace idempotencia.Interfaces;

public interface IAuthService
{

    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);

    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);

    Task<AuthResponse> ExternalLoginAsync(
        string provider, string providerUserId, string email, string fullName,
        CancellationToken ct = default);
}
