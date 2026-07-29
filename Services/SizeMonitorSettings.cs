namespace idempotencia.Services;

/// <summary>
/// Configuración del job que sincroniza <c>CurrentSizeMB</c> contra el tamaño
/// real de cada BD en su motor. Se enlaza desde la sección
/// <c>Provisioning:SizeMonitor</c> de <c>appsettings.json</c>. Ver
/// <see cref="DatabaseSizeMonitor"/> y docs/bugs.md ítem 25.
///
/// Todos los valores tienen un default razonable: si la sección no existe en
/// absoluto, el job corre cada 15 minutos.
/// </summary>
public class SizeMonitorSettings
{
    public const string SectionName = "Provisioning:SizeMonitor";

    /// <summary>
    /// Permite apagar el job sin desplegar. Útil en local, donde normalmente no
    /// hay los cuatro motores levantados y el job solo generaría ruido de
    /// errores de conexión en los logs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cada cuánto se recorren todas las BDs activas. 15 minutos es un
    /// compromiso: suficientemente fresco para que el estudiante vea el efecto
    /// de lo que acaba de cargar, y suficientemente espaciado para no tener
    /// conexiones permanentes contra los cuatro motores. Valores por debajo de
    /// 1 se ignoran y se usa 1.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Espera antes del primer ciclo. Evita competir por recursos con el
    /// arranque de la aplicación y le da margen a los motores para estar listos
    /// cuando todo el stack se levanta a la vez con docker compose.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Tope de tiempo para medir UNA base. Sin esto, un motor que acepta la
    /// conexión pero no responde dejaría el ciclo colgado indefinidamente y
    /// ninguna de las BDs siguientes se actualizaría.
    /// </summary>
    public int PerDatabaseTimeoutSeconds { get; set; } = 30;
}
