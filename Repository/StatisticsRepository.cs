using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Repository;

/// <summary>
/// Implementación de <see cref="IStatisticsRepository"/>. Invoca el SP/View de
/// métricas; el cálculo de las estadísticas vive en SQL Server.
/// </summary>
public class StatisticsRepository : IStatisticsRepository
{
    private readonly ColmenaDbContext _db;

    public StatisticsRepository(ColmenaDbContext db) => _db = db;

    public async Task<PlatformStatistics> GetPlatformStatisticsAsync(CancellationToken ct = default)
    {
        var result = await _db.PlatformStatistics
            .FromSqlRaw("EXEC sp_GetPlatformStatistics")
            .AsNoTracking()
            .ToListAsync(ct);

        return result.First();
    }
}
