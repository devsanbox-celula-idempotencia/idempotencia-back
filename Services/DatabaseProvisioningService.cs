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
    private readonly ILogger<DatabaseProvisioningService> _logger;

    public DatabaseProvisioningService(
        IDatabaseRepository repo,
        IDatabaseProvisionerFactory factory,
        ILogger<DatabaseProvisioningService> logger)
    {
        _repo = repo;
        _factory = factory;
        _logger = logger;
    }

    public async Task<CreateDatabaseResponse> ProvisionAsync(
        int userId, string engine, string dbName, CancellationToken ct = default)
    {
        // 1. Valida que el motor esté soportado (400 si no) ANTES de tocar la DB.
        var provisioner = _factory.Get(engine);

        // 2. Reserva en el catálogo: el SP valida cuota/límites y genera nombres.
        var reservation = await _repo.ReserveDatabaseAsync(userId, engine, dbName, ct);

        // 3. Contraseña generada en el backend (solo se devuelve una vez).
        var password = PasswordGenerator.Generate();

        try
        {
            // 4. Creación física en el motor correspondiente.
            var result = await provisioner.CreateAsync(
                reservation.DbName, reservation.LoginName, password, reservation.MaxStorageMB, ct);

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
}
