using idempotencia.DTOs;
using idempotencia.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace idempotencia.Controllers;

/// <summary>
/// Estadísticas generales de la plataforma. Restringido a administradores:
/// token inválido/expirado → 401, usuario sin rol Admin → 403.
/// </summary>
[ApiController]
[Route("statistics")]
[Authorize(Roles = "Admin")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsRepository _statistics;

    public StatisticsController(IStatisticsRepository statistics) => _statistics = statistics;

    [HttpGet]
    public async Task<ActionResult<PlatformStatisticsResponse>> GetPlatformStatistics(CancellationToken ct)
    {
        var stats = await _statistics.GetPlatformStatisticsAsync(ct);

        return Ok(new PlatformStatisticsResponse
        {
            TotalUsers = stats.TotalUsers,
            ActiveUsers = stats.ActiveUsers,
            TotalDatabases = stats.TotalDatabases,
            ActiveDatabases = stats.ActiveDatabases,
            TotalLogins = stats.TotalLogins,
            ServiceAvailable = true
        });
    }
}
