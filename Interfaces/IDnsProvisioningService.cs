using idempotencia.DTOs;

namespace idempotencia.Interfaces;

/// <summary>
/// Orquesta el ciclo de vida de los subdominios: coordina el catálogo (SPs de
/// control) con el proveedor de DNS externo. Es el equivalente de
/// <see cref="IDatabaseProvisioningService"/> para el servicio de DNS, y existe
/// por el mismo motivo: el controller no debe conocer el orden en que hay que
/// tocar catálogo y sistema externo ni cómo revertir si algo falla a mitad.
/// </summary>
public interface IDnsProvisioningService
{
    /// <summary>
    /// Crea un subdominio <c>{label}.{cell}.{zona}</c> para el usuario: reserva
    /// en el catálogo → crea el registro A proxeado en el proveedor → confirma.
    /// Si el proveedor falla, intenta limpiar el registro y marca la reserva
    /// como fallida antes de propagar el error.
    /// </summary>
    Task<DnsRecordResponse> CreateAsync(
        int userId, CreateDnsRecordRequest request, CancellationToken ct = default);

    /// <summary>Lista los subdominios del usuario autenticado.</summary>
    Task<IReadOnlyList<DnsRecordResponse>> GetMineAsync(
        int userId, CancellationToken ct = default);

    /// <summary>
    /// Detalle de un subdominio puntual. 404 si no existe o no es del usuario.
    /// </summary>
    Task<DnsRecordResponse> GetDetailAsync(
        int userId, int dnsRecordId, CancellationToken ct = default);

    /// <summary>
    /// Reapunta un subdominio a otra IP. Aplica primero en el proveedor y
    /// después en el catálogo, para que un fallo no deje al catálogo prometiendo
    /// un destino que el DNS no resuelve.
    /// </summary>
    Task<DnsRecordResponse> UpdateAsync(
        int userId, int dnsRecordId, UpdateDnsRecordRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Elimina el subdominio del usuario en el proveedor y lo marca como borrado
    /// en el catálogo. A diferencia de las bases de datos, no exige un paso
    /// previo de desactivación: borrar un registro DNS no destruye datos del
    /// usuario y es reversible volviéndolo a crear con el mismo nombre.
    /// </summary>
    Task DeleteAsync(int userId, int dnsRecordId, CancellationToken ct = default);

    // -----------------------------------------------------------------------
    // Administración (rol Admin)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Listado de auditoría: todos los registros de todos los usuarios, con
    /// filtros opcionales por célula, dueño, estado y días sin modificar.
    /// </summary>
    Task<IReadOnlyList<AdminDnsRecordResponse>> GetAllAsync(
        string? cell, int? userId, string? status, int? minDaysSinceUpdate,
        CancellationToken ct = default);

    /// <summary>
    /// Detalle de cualquier registro, sin filtro de propiedad. Incluye los ya
    /// terminados (<c>Deleted</c>/<c>Revoked</c>).
    /// </summary>
    Task<AdminDnsRecordResponse> GetDetailAdminAsync(
        int dnsRecordId, CancellationToken ct = default);

    /// <summary>
    /// Revoca el subdominio de un usuario: lo elimina en el proveedor y lo marca
    /// como <c>Revoked</c> en el catálogo, dejando constancia de quién lo revocó
    /// y por qué.
    /// </summary>
    Task RevokeAsync(
        int adminUserId, int dnsRecordId, string reason, CancellationToken ct = default);
}
