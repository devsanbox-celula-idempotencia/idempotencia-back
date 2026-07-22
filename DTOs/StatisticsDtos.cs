namespace idempotencia.DTOs;

/// <summary>Estadísticas generales de la plataforma (solo administradores).</summary>
public class PlatformStatisticsResponse
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int TotalDatabases { get; set; }
    public int ActiveDatabases { get; set; }
    public int TotalLogins { get; set; }

    /// <summary>Disponibilidad del servicio: true si las métricas se obtuvieron.</summary>
    public bool ServiceAvailable { get; set; }
}
