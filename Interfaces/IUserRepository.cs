using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Abstracción de acceso a datos de usuarios. Las implementaciones concretas
/// SOLO invocan Stored Procedures; no contienen lógica de negocio ni LINQ de
/// dominio. Los controllers/servicios dependen de esta interfaz (DIP).
/// </summary>
public interface IUserRepository
{
    /// <summary>Invoca <c>sp_GetLoginByEmail</c>. Devuelve null si no existe.</summary>
    Task<LoginInfo?> GetLoginByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Invoca <c>sp_RegisterUser</c> y devuelve la identidad creada.</summary>
    Task<UserIdentity> RegisterUserAsync(
        string email, string passwordHash, string fullName, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_UpsertExternalLogin</c>: busca/crea/vincula el usuario OAuth
    /// y devuelve su identidad resuelta.
    /// </summary>
    Task<UserIdentity> UpsertExternalLoginAsync(
        string provider, string providerUserId, string email, string fullName,
        CancellationToken ct = default);
}
