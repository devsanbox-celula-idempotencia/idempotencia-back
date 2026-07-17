using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Repository;

/// <summary>
/// Implementación de <see cref="IDatabaseRepository"/>. SOLO invoca Stored
/// Procedures con parámetros <see cref="SqlParameter"/> tipados. Toda la lógica
/// (cuotas, límites, creación de login) vive en el SP.
/// </summary>
public class DatabaseRepository : IDatabaseRepository
{
    private readonly ColmenaDbContext _db;

    public DatabaseRepository(ColmenaDbContext db) => _db = db;

    public async Task<NewDatabaseResult> CreateDatabaseAsync(
        int userId, string dbName, CancellationToken ct = default)
    {
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };
        var pDbName = new SqlParameter("@DbName", System.Data.SqlDbType.NVarChar, 128) { Value = dbName };

        var result = await _db.NewDatabaseResults
            .FromSqlRaw("EXEC sp_CreateDatabase @UserId, @DbName", pUserId, pDbName)
            .AsNoTracking()
            .ToListAsync(ct);

        // El SP devuelve el registro de la BD aprovisionada con sus credenciales.
        return result.First();
    }

    public async Task<IReadOnlyList<ProvisionedDatabaseInfo>> GetUserDatabasesAsync(
        int userId, CancellationToken ct = default)
    {
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        var result = await _db.ProvisionedDatabases
            .FromSqlRaw("EXEC sp_GetUserDatabases @UserId", pUserId)
            .AsNoTracking()
            .ToListAsync(ct);

        return result;
    }
}
