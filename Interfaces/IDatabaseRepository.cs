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

    /// <summary>
    /// Invoca <c>sp_GetDatabaseDetail</c>: trae una BD puntual, validando que
    /// pertenezca a <paramref name="userId"/> (el SP filtra por ambos; si no
    /// hay fila, es porque no existe o no es del usuario — mismo resultado a
    /// propósito, ver <see cref="idempotencia.Middleware.NotFoundException"/>).
    /// Incluye <c>LoginName</c>, a diferencia de <see cref="GetUserDatabasesAsync"/>.
    /// </summary>
    Task<ProvisionedDatabaseDetail?> GetDatabaseDetailAsync(
        int databaseId, int userId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_DeactivateDatabase</c>: marca la BD como 'Inactive' y
    /// registra <c>PausedAt</c>. El SP re-valida ownership y que el estado
    /// actual sea 'Active' (defensa en profundidad; la validación principal ya
    /// se hizo en el servicio antes de tocar el motor físico).
    /// </summary>
    Task DeactivateDatabaseAsync(int databaseId, int userId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_DeleteDatabase</c>: marca la BD como 'Deleted' y registra
    /// <c>DeletedAt</c>. Se llama DESPUÉS de que el borrado físico
    /// (<see cref="idempotencia.Interfaces.IDatabaseProvisioner.DropAsync"/>)
    /// ya se ejecutó con éxito.
    /// </summary>
    Task MarkDatabaseDeletedAsync(int databaseId, int userId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_ResetDatabasePassword</c>: guarda el nuevo hash de
    /// contraseña en el catálogo. Se llama DESPUÉS de que el cambio físico
    /// (<see cref="idempotencia.Interfaces.IDatabaseProvisioner.ChangePasswordAsync"/>)
    /// ya se ejecutó con éxito.
    /// </summary>
    Task ResetDatabasePasswordAsync(
        int databaseId, int userId, string newPasswordHash, CancellationToken ct = default);
}
