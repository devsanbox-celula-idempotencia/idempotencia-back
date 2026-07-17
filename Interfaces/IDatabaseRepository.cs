using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Abstracción de acceso al CATÁLOGO de bases de datos aprovisionadas. Las
/// implementaciones SOLO invocan Stored Procedures de control. La creación
/// física en cada motor NO va aquí: la hace un <see cref="IDatabaseProvisioner"/>.
///
/// Flujo multi-motor: reservar (valida cuota/límites y genera nombres) → el
/// provisioner crea en el motor → confirmar o marcar fallida.
/// </summary>
public interface IDatabaseRepository
{
    /// <summary>
    /// Invoca <c>sp_ReserveDatabase</c>: valida cuota/límites, inserta el registro
    /// con Status='Provisioning' y devuelve los nombres generados.
    /// </summary>
    Task<DatabaseReservation> ReserveDatabaseAsync(
        int userId, string engine, string dbName, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_ConfirmDatabase</c>: marca la BD como 'Active' y guarda el
    /// hash de las credenciales tras la creación física exitosa.
    /// </summary>
    Task ConfirmDatabaseAsync(int databaseId, string passwordHash, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_FailDatabase</c>: revierte la reserva (marca fallida/borra) si
    /// la creación física en el motor falló.
    /// </summary>
    Task FailDatabaseAsync(int databaseId, CancellationToken ct = default);

    /// <summary>Invoca <c>sp_GetUserDatabases</c>: lista las BDs del usuario.</summary>
    Task<IReadOnlyList<ProvisionedDatabaseInfo>> GetUserDatabasesAsync(
        int userId, CancellationToken ct = default);
}
