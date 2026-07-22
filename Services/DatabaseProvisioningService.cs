using idempotencia.DTOs;
using idempotencia.Interfaces;

namespace idempotencia.Services;

/// <summary>
/// Orquesta el aprovisionamiento multi-motor. Coordina el catálogo (SPs de
/// control) con el provisioner físico del motor. La lógica de negocio (cuotas,
/// límites, nombres) vive en el SP <c>sp_ReserveDatabase</c>; aquí solo se
/// coordina el flujo reservar → crear → confirmar / revertir.
/// </summary>
public class DatabaseProvisioningService : IDatabaseProvisioningService
{
    private readonly IDatabaseRepository _repo;
    private readonly IDatabaseProvisionerFactory _factory;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseProvisioningService> _logger;

    public DatabaseProvisioningService(
        IDatabaseRepository repo,
        IDatabaseProvisionerFactory factory,
        IConfiguration config,
        ILogger<DatabaseProvisioningService> logger)
    {
        _repo = repo;
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    public async Task<CreateDatabaseResponse> ProvisionAsync(
        int userId, string engine, string dbName,
        int? requestedMaxConcurrentConnections = null, CancellationToken ct = default)
    {
        // 1. Valida que el motor esté soportado (400 si no) ANTES de tocar la DB.
        var provisioner = _factory.Get(engine);

        // 1b. Resuelve el tope de conexiones concurrentes: si el cliente pidió
        // uno, se acota SIEMPRE al cap del motor (nunca se confía en lo que
        // pide el cliente sin límite); si no pidió nada, se usa el default del
        // motor. En motores sin soporte nativo (SqlServer/Mongo) el provisioner
        // simplemente ignora este valor.
        var effectiveMaxConcurrentConnections =
            ResolveMaxConcurrentConnections(engine, requestedMaxConcurrentConnections);

        // 2. Reserva en el catálogo: el SP valida cuota/límites y genera nombres.
        var reservation = await _repo.ReserveDatabaseAsync(userId, engine, dbName, ct);

        // 3. Contraseña generada en el backend (solo se devuelve una vez).
        var password = PasswordGenerator.Generate();

        try
        {
            // 4. Creación física en el motor correspondiente.
            var result = await provisioner.CreateAsync(
                reservation.DbName, reservation.LoginName, password, reservation.MaxStorageMB,
                effectiveMaxConcurrentConnections, ct);

            // 5. Confirma en el catálogo y guarda el HASH de la contraseña.
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);
            await _repo.ConfirmDatabaseAsync(reservation.DatabaseId, passwordHash, ct);

            return new CreateDatabaseResponse
            {
                DatabaseId = reservation.DatabaseId,
                Engine = engine,
                DbName = reservation.DbName,
                Status = "Active",
                MaxStorageMB = reservation.MaxStorageMB,
                MaxConcurrentConnections = effectiveMaxConcurrentConnections,
                Host = result.Host,
                Port = result.Port,
                LoginName = reservation.LoginName,
                Password = password
            };
        }
        catch (Exception ex)
        {
            // 6. Revertir: intentar limpiar el motor y marcar la reserva como fallida.
            _logger.LogError(ex, "Fallo al aprovisionar la BD {Db} ({Engine}); revirtiendo.",
                reservation.DbName, engine);

            try
            {
                await provisioner.DropAsync(reservation.DbName, reservation.LoginName, ct);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Fallo limpiando la BD {Db} tras error.", reservation.DbName);
            }

            try
            {
                await _repo.FailDatabaseAsync(reservation.DatabaseId, ct);
            }
            catch (Exception failEx)
            {
                _logger.LogError(failEx, "Fallo marcando la reserva {Id} como fallida.",
                    reservation.DatabaseId);
            }

            throw; // se propaga al middleware para la respuesta de error uniforme
        }
    }

    /// <summary>
    /// Lee <c>Provisioning:{Engine}:MaxConcurrentConnections</c> (default,
    /// usado si el cliente no pidió nada) y
    /// <c>Provisioning:{Engine}:MaxConcurrentConnectionsCap</c> (tope duro) de
    /// configuración, y devuelve el valor final ya acotado. El cliente NUNCA
    /// puede superar el cap sin importar lo que pida — de lo contrario este
    /// control de abuso quedaría en manos del propio usuario que se quiere
    /// limitar.
    /// </summary>
    private int ResolveMaxConcurrentConnections(string engine, int? requested)
    {
        var defaultValue =
            int.TryParse(_config[$"Provisioning:{engine}:MaxConcurrentConnections"], out var d) ? d : 5;
        var cap =
            int.TryParse(_config[$"Provisioning:{engine}:MaxConcurrentConnectionsCap"], out var c) ? c : 20;

        var requestedOrDefault = requested ?? defaultValue;

        if (requestedOrDefault < 1) requestedOrDefault = 1;
        if (requestedOrDefault > cap) requestedOrDefault = cap;

        return requestedOrDefault;
    }
}
