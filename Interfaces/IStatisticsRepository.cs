using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Acceso a las métricas de la plataforma. La implementación SOLO invoca el
/// SP/View de estadísticas definido en SQL Server.
/// </summary>
public interface IStatisticsRepository
{
    Task<PlatformStatistics> GetPlatformStatisticsAsync(CancellationToken ct = default);
}
