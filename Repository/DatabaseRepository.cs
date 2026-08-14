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

    public async Task<ProvisionedDatabaseDetail?> GetDatabaseDetailAsync(
        int databaseId, int userId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        var result = await _db.ProvisionedDatabaseDetails
            .FromSqlRaw("EXEC sp_GetDatabaseDetail @DatabaseId, @UserId", pDatabaseId, pUserId)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.FirstOrDefault();
    }

    public async Task DeactivateDatabaseAsync(int databaseId, int userId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_DeactivateDatabase @DatabaseId, @UserId", new object[] { pDatabaseId, pUserId }, ct);
    }

    public async Task ReactivateDatabaseAsync(int databaseId, int userId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_ReactivateDatabase @DatabaseId, @UserId", new object[] { pDatabaseId, pUserId }, ct);
    }

    public async Task MarkDatabaseDeletedAsync(int databaseId, int userId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_DeleteDatabase @DatabaseId, @UserId", new object[] { pDatabaseId, pUserId }, ct);
    }

    public async Task ResetDatabasePasswordAsync(
        int databaseId, int userId, string newPasswordHash, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };
        var pHash = new SqlParameter("@PasswordHash", System.Data.SqlDbType.NVarChar, 255) { Value = newPasswordHash };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_ResetDatabasePassword @DatabaseId, @UserId, @PasswordHash",
            new object[] { pDatabaseId, pUserId, pHash }, ct);
    }

    public async Task<IReadOnlyList<ProvisionedDatabaseInfo>> GetDatabasesForSizeSyncAsync(
        CancellationToken ct = default)
    {
        // Sin parámetros: el job recorre todas las BDs activas de la
        // plataforma, no las de un usuario. Reusa el mismo tipo de resultado
        // que sp_GetUserDatabases porque el SP devuelve las mismas columnas.
        var result = await _db.ProvisionedDatabases
            .FromSqlRaw("EXEC sp_GetDatabasesForSizeSync")
            .AsNoTracking()
            .ToListAsync(ct);

        return result;
    }

    public async Task UpdateDatabaseSizeAsync(
        int databaseId, decimal currentSizeMb, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };

        // Precisión y escala explícitas para que coincidan con DECIMAL(10,2)
        // del SP y de la columna; sin esto el driver infiere escala 0 y
        // truncaría los decimales en silencio.
        var pSize = new SqlParameter("@CurrentSizeMB", System.Data.SqlDbType.Decimal)
        {
            Precision = 10,
            Scale = 2,
            Value = currentSizeMb
        };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_UpdateDatabaseSize @DatabaseId, @CurrentSizeMB",
            new object[] { pDatabaseId, pSize }, ct);
    }

    public async Task SetDatabaseExternalRefAsync(
        int databaseId, string externalId, string? externalDbName, string? externalLoginName,
        int? externalMaxStorageMb, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pExternalId = new SqlParameter("@ExternalId", System.Data.SqlDbType.NVarChar, 100)
        {
            Value = externalId
        };

        // DBNull explícito y no null de C#: SqlParameter con Value = null se
        // envía como "parámetro sin asignar" y el SP recibiría basura en vez de
        // NULL. Pasa cuando el servicio externo no reporta un nombre físico.
        var pExternalDbName = new SqlParameter("@ExternalDbName", System.Data.SqlDbType.NVarChar, 128)
        {
            Value = (object?)externalDbName ?? DBNull.Value
        };

        var pExternalLoginName = new SqlParameter("@ExternalLoginName", System.Data.SqlDbType.NVarChar, 128)
        {
            Value = (object?)externalLoginName ?? DBNull.Value
        };
        var pExternalMaxStorageMb = new SqlParameter("@ExternalMaxStorageMB", System.Data.SqlDbType.Int)
        {
            Value = (object?)externalMaxStorageMb ?? DBNull.Value
        };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_SetDatabaseExternalRef @DatabaseId, @ExternalId, @ExternalDbName, " +
            "@ExternalLoginName, @ExternalMaxStorageMB",
            new object[]
            {
                pDatabaseId, pExternalId, pExternalDbName, pExternalLoginName, pExternalMaxStorageMb
            }, ct);
    }

    public async Task<ExternalDatabaseRef?> GetDatabaseExternalRefAsync(
        int databaseId, int userId, CancellationToken ct = default)
    {
        var pDatabaseId = new SqlParameter("@DatabaseId", System.Data.SqlDbType.Int) { Value = databaseId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        var result = await _db.ExternalDatabaseRefs
            .FromSqlRaw("EXEC sp_GetDatabaseExternalRef @DatabaseId, @UserId", pDatabaseId, pUserId)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.FirstOrDefault();
    }
}
