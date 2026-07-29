using idempotencia.Interfaces;
using Microsoft.Extensions.Options;

namespace idempotencia.Services;

/// <summary>
/// Job en background que mantiene <c>ProvisionedDatabases.CurrentSizeMB</c>
/// sincronizado con el tamaño real de cada base en su motor.
///
/// Existe porque el catálogo vive en SQL Server y no puede medir bases de
/// MySQL, PostgreSQL ni MongoDB — están en otros motores, con otros protocolos.
/// Antes de este job la columna se fijaba en la creación y no volvía a cambiar
/// nunca, así que la API reportaba un tamaño que jamás correspondía a la
/// realidad (docs/bugs.md ítem 25).
///
/// Principio de diseño: el job es de MEJOR ESFUERZO y nunca debe tumbar la
/// aplicación. Un motor caído, una BD borrada por fuera o un timeout afectan
/// como mucho a la medición de esa base; el ciclo continúa con las demás y el
/// siguiente ciclo vuelve a intentarlo. Por eso todas las excepciones se
/// atrapan y se registran en vez de propagarse: un <see cref="BackgroundService"/>
/// que deja escapar una excepción tumba el host completo.
/// </summary>
public class DatabaseSizeMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SizeMonitorSettings _settings;
    private readonly ILogger<DatabaseSizeMonitor> _logger;

    public DatabaseSizeMonitor(
        IServiceScopeFactory scopeFactory,
        IOptions<SizeMonitorSettings> settings,
        ILogger<DatabaseSizeMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation(
                "Sincronización de tamaños DESHABILITADA (Provisioning:SizeMonitor:Enabled=false). " +
                "CurrentSizeMB no se actualizará y la API seguirá reportando el valor de creación.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.IntervalMinutes));

        _logger.LogInformation(
            "Sincronización de tamaños habilitada: primer ciclo en {Delay}s, luego cada {Interval} min.",
            _settings.StartupDelaySeconds, interval.TotalMinutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.StartupDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // la app se está apagando durante el delay inicial
        }

        using var timer = new PeriodicTimer(interval);

        do
        {
            // RunCycleAsync ya atrapa todo lo suyo; este try es la última red
            // de seguridad para que ninguna excepción inesperada salga de
            // ExecuteAsync y tumbe el host.
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Fallo no controlado en el ciclo de sincronización de tamaños. " +
                    "Se reintentará en el siguiente ciclo.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Un ciclo completo: lista las BDs activas y mide cada una. Se abre un
    /// scope de DI propio porque <c>IDatabaseRepository</c> y el factory de
    /// provisioners están registrados como Scoped y este servicio es Singleton
    /// (inyectarlos por constructor daría un captive dependency).
    /// </summary>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDatabaseRepository>();
        var factory = scope.ServiceProvider.GetRequiredService<IDatabaseProvisionerFactory>();

        var databases = await repo.GetDatabasesForSizeSyncAsync(ct);

        if (databases.Count == 0)
        {
            _logger.LogDebug("Sincronización de tamaños: no hay bases activas que medir.");
            return;
        }

        int actualizadas = 0, sinCambio = 0, fallidas = 0;

        foreach (var db in databases)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // Timeout por base: un motor que acepta la conexión pero no
                // responde no puede bloquear la medición de las demás.
                using var perDb = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perDb.CancelAfter(TimeSpan.FromSeconds(_settings.PerDatabaseTimeoutSeconds));

                var provisioner = factory.Get(db.Engine);
                var measured = await provisioner.GetSizeMbAsync(db.DbName, perDb.Token);

                // Se escribe solo si cambió. El caso normal es que la mayoría de
                // las bases no se muevan entre ciclos; evitar el UPDATE ahorra
                // escrituras al catálogo y deja los logs de EF legibles.
                if (measured == db.CurrentSizeMB)
                {
                    sinCambio++;
                    continue;
                }

                await repo.UpdateDatabaseSizeAsync(db.DatabaseId, measured, ct);
                actualizadas++;

                _logger.LogDebug(
                    "BD {DatabaseId} ({Engine}/{DbName}): {Antes} MB -> {Ahora} MB.",
                    db.DatabaseId, db.Engine, db.DbName, db.CurrentSizeMB, measured);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // la app se está apagando: cortar el ciclo, no contar como fallo
            }
            catch (Exception ex)
            {
                fallidas++;
                _logger.LogWarning(ex,
                    "No se pudo medir la BD {DatabaseId} ({Engine}/{DbName}). " +
                    "Conserva su último valor conocido y se reintenta en el próximo ciclo.",
                    db.DatabaseId, db.Engine, db.DbName);
            }
        }

        // Un solo resumen por ciclo en Information; el detalle por base va en
        // Debug para no inundar los logs cada 15 minutos.
        _logger.LogInformation(
            "Sincronización de tamaños: {Total} bases activas — {Actualizadas} actualizadas, " +
            "{SinCambio} sin cambio, {Fallidas} con error.",
            databases.Count, actualizadas, sinCambio, fallidas);
    }

    /// <summary>
    /// <c>PeriodicTimer.WaitForNextTickAsync</c> lanza si el token se cancela
    /// mientras espera. Acá eso no es un error: es el apagado normal de la
    /// aplicación, y se traduce a "no hay más ciclos".
    /// </summary>
    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
