using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Repository;

/// <summary>
/// Implementación de <see cref="IUserRepository"/>. SOLO invoca Stored
/// Procedures mediante <c>FromSqlRaw</c> y parámetros <see cref="SqlParameter"/>
/// tipados. Prohibida la concatenación de strings en SQL.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly ColmenaDbContext _db;

    public UserRepository(ColmenaDbContext db) => _db = db;

    public async Task<LoginInfo?> GetLoginByEmailAsync(string email, CancellationToken ct = default)
    {
        // Parámetro tipado; el SP resuelve la lógica de búsqueda.
        var pEmail = new SqlParameter("@Email", System.Data.SqlDbType.NVarChar, 150) { Value = email };

        var result = await _db.LoginInfos
            .FromSqlRaw("EXEC sp_GetLoginByEmail @Email", pEmail)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.FirstOrDefault();
    }

    public async Task<UserIdentity> RegisterUserAsync(
        string email, string passwordHash, string fullName, CancellationToken ct = default)
    {
        var pEmail = new SqlParameter("@Email", System.Data.SqlDbType.NVarChar, 150) { Value = email };
        var pHash = new SqlParameter("@PasswordHash", System.Data.SqlDbType.NVarChar, 255) { Value = passwordHash };
        var pName = new SqlParameter("@FullName", System.Data.SqlDbType.NVarChar, 150) { Value = fullName };

        var result = await _db.UserIdentities
            .FromSqlRaw("EXEC sp_RegisterUser @Email, @PasswordHash, @FullName", pEmail, pHash, pName)
            .AsNoTracking()
            .ToListAsync(ct);

        // Se espera que el SP devuelva exactamente la identidad del usuario creado.
        return result.First();
    }

    public async Task<UserIdentity> UpsertExternalLoginAsync(
        string provider, string providerUserId, string email, string fullName,
        CancellationToken ct = default)
    {
        var pProvider = new SqlParameter("@Provider", System.Data.SqlDbType.NVarChar, 30) { Value = provider };
        var pProviderUserId = new SqlParameter("@ProviderUserId", System.Data.SqlDbType.NVarChar, 200) { Value = providerUserId };
        var pEmail = new SqlParameter("@Email", System.Data.SqlDbType.NVarChar, 150) { Value = email };
        var pName = new SqlParameter("@FullName", System.Data.SqlDbType.NVarChar, 150) { Value = fullName };

        var result = await _db.UserIdentities
            .FromSqlRaw(
                "EXEC sp_UpsertExternalLogin @Provider, @ProviderUserId, @Email, @FullName",
                pProvider, pProviderUserId, pEmail, pName)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.First();
    }
}
