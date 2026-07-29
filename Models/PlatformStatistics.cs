namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) del SP/View de métricas <c>sp_GetPlatformStatistics</c>.
/// </summary>
public class PlatformStatistics
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int TotalDatabases { get; set; }
    public int ActiveDatabases { get; set; }
    public int TotalLogins { get; set; }
}
