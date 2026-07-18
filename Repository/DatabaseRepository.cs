using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Repository;

/// <summary>
/// Implementación de <see cref="IDatabaseRepository"/>. SOLO invoca Stored
/// Procedures de control con parámetros <see cref="SqlParameter"/> tipados.
/// Toda la lógica (cuotas, límites, generación de nombres) vive en los SPs.
/// </summary>
public class DatabaseRepository : IDatabaseRepository
{
    private readonly ColmenaDbContext _db;

    public DatabaseRepository(ColmenaDbContext db) => _db = db;

    public async Task<DatabaseReservation> ReserveDatabaseAsync(
        int userId, string engine, string dbName, CancellationToken ct = default)
    {
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };
        var pEngine = new SqlParameter("@Engine", System.Data.SqlDbType.NVarChar, 20) { Value = engine };
        var pDbName = new SqlParameter("@DbName", System.Data.SqlDbType.NVarChar, 128) { Value = dbName };

        var result = await _db.DatabaseReservations
            .FromSqlRaw("EXEC sp_ReserveDatabase @UserId, @Engine, @DbName", pUserId, pEngine, pDbName)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.First();
    }

    public async Task ConfirmDatabaseAsync(int databaseId, string passwordHash, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pHash = new SqlParameter("@PasswordHash", System.Data.SqlDbType.NVarChar, 255) { Value = passwordHash };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_ConfirmDatabase @DatabaseId, @PasswordHash", new object[] { pDatabaseId, pHash }, ct);
    }

    public async Task FailDatabaseAsync(int databaseId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_FailDatabase @DatabaseId", new object[] { pDatabaseId }, ct);
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
