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
    /// Invoca <c>sp_ReactivateDatabase</c>: devuelve la BD a 'Active', limpia
    /// <c>PausedAt</c> y refresca <c>LastActivityAt</c> (reactivar es una
    /// acción explícita del usuario, a diferencia de la medición de tamaño).
    /// El SP re-valida ownership y que el estado actual sea 'Inactive'.
    /// </summary>
    Task ReactivateDatabaseAsync(int databaseId, int userId, CancellationToken ct = default);

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

    /// <summary>
    /// Invoca <c>sp_GetDatabasesForSizeSync</c>: devuelve TODAS las BDs
    /// 'Active' de TODOS los usuarios. Es el único método del repositorio que
    /// no filtra por usuario, a propósito: lo consume
    /// <c>DatabaseSizeMonitor</c>, un job del sistema que no actúa en nombre de
    /// ningún usuario autenticado. No exponerlo desde un controller.
    /// </summary>
    Task<IReadOnlyList<ProvisionedDatabaseInfo>> GetDatabasesForSizeSyncAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_UpdateDatabaseSize</c>: persiste el tamaño real medido por
    /// el <see cref="IDatabaseProvisioner"/> del motor correspondiente. Ver
    /// docs/bugs.md ítem 25.
    /// </summary>
    Task UpdateDatabaseSizeAsync(
        int databaseId, decimal currentSizeMb, CancellationToken ct = default);

    /// <summary>
    /// Guarda la referencia con la que un servicio EXTERNO de aprovisionamiento
    /// conoce esta BD (<c>sp_SetDatabaseExternalRef</c>). Solo se llama para los
    /// motores delegados en una API ajena; en los locales no hay nada que
    /// guardar porque el backend direcciona por nombre.
    ///
    /// No recibe <c>userId</c> a propósito: es una escritura del sistema dentro
    /// del mismo flujo que ya validó la propiedad al reservar, igual que
    /// <see cref="UpdateDatabaseSizeAsync"/>.
    /// </summary>
    Task SetDatabaseExternalRefAsync(
        int databaseId, string externalId, string? externalDbName, CancellationToken ct = default);

    /// <summary>
    /// Lee la referencia externa de una BD del usuario
    /// (<c>sp_GetDatabaseExternalRef</c>). Devuelve un objeto con ambos campos
    /// en <c>null</c> cuando la BD no la administra ningún servicio externo —el
    /// caso de los cuatro motores locales— y <c>null</c> si la BD no existe o no
    /// es del usuario.
    /// </summary>
    Task<ExternalDatabaseRef?> GetDatabaseExternalRefAsync(
        int databaseId, int userId, CancellationToken ct = default);
}
