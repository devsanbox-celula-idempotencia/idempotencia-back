using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Repository;

/// <summary>
/// Implementación de <see cref="IDnsRepository"/>. Igual que
/// <see cref="DatabaseRepository"/>: SOLO invoca Stored Procedures de control
/// con parámetros <see cref="SqlParameter"/> tipados. Toda la lógica (cuota por
/// usuario, etiquetas reservadas, unicidad del FQDN) vive en los SPs.
/// </summary>
public class DnsRepository : IDnsRepository
{
    private readonly ColmenaDbContext _db;

    public DnsRepository(ColmenaDbContext db) => _db = db;

    public async Task<DnsRecordReservation> ReserveDnsRecordAsync(
        int userId, string label, string cell, string zoneName, string recordType,
        string content, bool proxied, int ttl, CancellationToken ct = default)
    {
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };
        var pLabel = new SqlParameter("@Label", System.Data.SqlDbType.NVarChar, 63) { Value = label };
        var pCell = new SqlParameter("@Cell", System.Data.SqlDbType.NVarChar, 63) { Value = cell };
        var pZone = new SqlParameter("@ZoneName", System.Data.SqlDbType.NVarChar, 127) { Value = zoneName };
        var pType = new SqlParameter("@RecordType", System.Data.SqlDbType.NVarChar, 10) { Value = recordType };
        var pContent = new SqlParameter("@Content", System.Data.SqlDbType.NVarChar, 255) { Value = content };
        var pProxied = new SqlParameter("@Proxied", System.Data.SqlDbType.Bit) { Value = proxied };
        var pTtl = new SqlParameter("@Ttl", System.Data.SqlDbType.Int) { Value = ttl };

        var result = await _db.DnsRecordReservations
            .FromSqlRaw(
                "EXEC sp_ReserveDnsRecord @UserId, @Label, @Cell, @ZoneName, @RecordType, @Content, @Proxied, @Ttl",
                pUserId, pLabel, pCell, pZone, pType, pContent, pProxied, pTtl)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.First();
    }

    public async Task ConfirmDnsRecordAsync(
        int dnsRecordId, string providerRecordId, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };
        var pProviderId = new SqlParameter("@ProviderRecordId", System.Data.SqlDbType.NVarChar, 64)
            { Value = providerRecordId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_ConfirmDnsRecord @DnsRecordId, @ProviderRecordId",
            new object[] { pId, pProviderId }, ct);
    }

    public async Task FailDnsRecordAsync(int dnsRecordId, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_FailDnsRecord @DnsRecordId", new object[] { pId }, ct);
    }

    public async Task<IReadOnlyList<DnsRecordInfo>> GetUserDnsRecordsAsync(
        int userId, CancellationToken ct = default)
    {
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        return await _db.DnsRecords
            .FromSqlRaw("EXEC sp_GetUserDnsRecords @UserId", pUserId)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<DnsRecordDetail?> GetDnsRecordDetailAsync(
        int dnsRecordId, int userId, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        var result = await _db.DnsRecordDetails
            .FromSqlRaw("EXEC sp_GetDnsRecordDetail @DnsRecordId, @UserId", pId, pUserId)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.FirstOrDefault();
    }

    public async Task UpdateDnsRecordAsync(
        int dnsRecordId, int userId, string content, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };
        var pContent = new SqlParameter("@Content", System.Data.SqlDbType.NVarChar, 255) { Value = content };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_UpdateDnsRecord @DnsRecordId, @UserId, @Content",
            new object[] { pId, pUserId, pContent }, ct);
    }

    public async Task MarkDnsRecordDeletedAsync(
        int dnsRecordId, int userId, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int) { Value = userId };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_DeleteDnsRecord @DnsRecordId, @UserId", new object[] { pId, pUserId }, ct);
    }

    // -----------------------------------------------------------------------
    // Administración
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<DnsRecordAdminInfo>> GetAllDnsRecordsAsync(
        string? cell, int? userId, string? status, int? minDaysSinceUpdate,
        CancellationToken ct = default)
    {
        // DBNull.Value y no null: un SqlParameter con Value = null (de C#) hace
        // que el driver omita el parámetro y el SP reciba su valor por defecto,
        // no NULL. Con filtros opcionales eso significaría filtrar por algo que
        // nadie pidió, así que la ausencia se expresa explícitamente.
        var pCell = new SqlParameter("@Cell", System.Data.SqlDbType.NVarChar, 63)
            { Value = (object?)cell ?? DBNull.Value };
        var pUserId = new SqlParameter("@UserId", System.Data.SqlDbType.Int)
            { Value = (object?)userId ?? DBNull.Value };
        var pStatus = new SqlParameter("@Status", System.Data.SqlDbType.NVarChar, 20)
            { Value = (object?)status ?? DBNull.Value };
        var pMinDays = new SqlParameter("@MinDaysSinceUpdate", System.Data.SqlDbType.Int)
            { Value = (object?)minDaysSinceUpdate ?? DBNull.Value };

        return await _db.DnsRecordAdminInfos
            .FromSqlRaw(
                "EXEC sp_GetAllDnsRecords @Cell, @UserId, @Status, @MinDaysSinceUpdate",
                pCell, pUserId, pStatus, pMinDays)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<DnsRecordDetail?> GetDnsRecordDetailAdminAsync(
        int dnsRecordId, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };

        var result = await _db.DnsRecordDetails
            .FromSqlRaw("EXEC sp_GetDnsRecordDetailAdmin @DnsRecordId", pId)
            .AsNoTracking()
            .ToListAsync(ct);

        return result.FirstOrDefault();
    }

    public async Task RevokeDnsRecordAsync(
        int dnsRecordId, int revokedByUserId, string reason, CancellationToken ct = default)
    {
        var pId = new SqlParameter("@DnsRecordId", System.Data.SqlDbType.Int) { Value = dnsRecordId };
        var pBy = new SqlParameter("@RevokedByUserId", System.Data.SqlDbType.Int) { Value = revokedByUserId };
        var pReason = new SqlParameter("@Reason", System.Data.SqlDbType.NVarChar, 500) { Value = reason };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_RevokeDnsRecord @DnsRecordId, @RevokedByUserId, @Reason",
            new object[] { pId, pBy, pReason }, ct);
    }
}
