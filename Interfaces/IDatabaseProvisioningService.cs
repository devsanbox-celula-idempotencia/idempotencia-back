using idempotencia.DTOs;

namespace idempotencia.Interfaces;

/// <summary>
/// Orquesta el aprovisionamiento multi-motor: reserva en el catálogo (SP),
/// crea físicamente en el motor (provisioner) y confirma o revierte. La lógica
/// de negocio (cuotas/límites) vive en el SP; aquí solo va la coordinación.
/// </summary>
public interface IDatabaseProvisioningService
{
    /// <summary>
    /// <paramref name="requestedMaxConcurrentConnections"/> es lo que pidió el
    /// cliente (puede ser null); el servicio lo resuelve contra el default y
    /// el cap del motor antes de pasarlo al provisioner físico.
    /// </summary>
    Task<CreateDatabaseResponse> ProvisionAsync(
        int userId, string engine, string dbName,
        int? requestedMaxConcurrentConnections = null, CancellationToken ct = default);

    /// <summary>
    /// Trae el detalle de una BD del usuario (host/puerto/usuario/estado,
    /// nunca la contraseña). Lanza <see cref="idempotencia.Middleware.NotFoundException"/>
    /// si no existe o no es del usuario.
    /// </summary>
    Task<DatabaseDetailResponse> GetDetailAsync(int userId, int databaseId, CancellationToken ct = default);

    /// <summary>
    /// Revoca el acceso físico del login/usuario (sin borrar datos) y marca la
    /// BD como 'Inactive' en el catálogo. Requiere que esté 'Active'. Paso
    /// obligatorio antes de poder eliminarla con <see cref="DeleteAsync"/>.
    /// </summary>
    Task<DatabaseDetailResponse> DeactivateAsync(int userId, int databaseId, CancellationToken ct = default);

    /// <summary>
    /// Elimina físicamente la BD + usuario (irreversible) y la marca 'Deleted'
    /// en el catálogo. Requiere que esté 'Inactive' — de lo contrario lanza
    /// <see cref="idempotencia.Middleware.AppException"/> (400).
    /// </summary>
    Task DeleteAsync(int userId, int databaseId, CancellationToken ct = default);

    /// <summary>
    /// Genera una contraseña nueva, la aplica en el motor físico, actualiza el
    /// hash en el catálogo y la envía por correo a <paramref name="userEmail"/>
    /// — la contraseña NUNCA vuelve en la respuesta HTTP de este método/endpoint.
    /// Requiere que la BD esté 'Active'.
    /// </summary>
    Task ResetPasswordAsync(int userId, int databaseId, string userEmail, string userFullName, CancellationToken ct = default);
}
