using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Abstracción de acceso al CATÁLOGO de registros DNS. Igual que
/// <see cref="IDatabaseRepository"/>, las implementaciones SOLO invocan Stored
/// Procedures de control con parámetros tipados: la lógica de negocio (cuota por
/// usuario, etiquetas reservadas, unicidad del FQDN, formato) vive en los SPs.
///
/// Flujo de creación: reservar (valida y genera el FQDN) → el proveedor crea el
/// registro → confirmar o marcar fallida.
///
/// Los métodos de administración van al final y se distinguen por NO recibir
/// <c>userId</c> como filtro de propiedad: los invoca un operador con rol Admin
/// actuando sobre registros ajenos, no un usuario sobre los suyos. Esa
/// separación es deliberada — un método que acepta un <c>userId</c> opcional
/// para "a veces filtrar" es exactamente cómo se filtra de menos por accidente.
/// </summary>
public interface IDnsRepository
{
    /// <summary>
    /// Invoca <c>sp_ReserveDnsRecord</c>: valida cuota, formato y colisiones,
    /// inserta la fila con <c>Status='Provisioning'</c> y devuelve el FQDN ya
    /// armado junto con los valores efectivos del registro.
    ///
    /// <paramref name="zoneName"/> lo aporta el backend (<c>Dns:ZoneName</c>) en
    /// vez de estar cableado en el SP, para que el dominio se configure en UN
    /// solo lugar. El SP lo usa para componer el FQDN y lo persiste ya
    /// compuesto: a partir de ahí el catálogo es la única fuente del nombre real.
    /// </summary>
    Task<DnsRecordReservation> ReserveDnsRecordAsync(
        int userId, string label, string cell, string zoneName, string recordType,
        string content, bool proxied, int ttl, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_ConfirmDnsRecord</c>: marca el registro como <c>'Active'</c>
    /// y guarda el identificador que asignó el proveedor.
    /// </summary>
    Task ConfirmDnsRecordAsync(
        int dnsRecordId, string providerRecordId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_FailDnsRecord</c>: marca la reserva como <c>'Failed'</c>
    /// cuando la creación en el proveedor no salió. La fila se conserva para
    /// poder diagnosticar, pero deja de ocupar el FQDN.
    /// </summary>
    Task FailDnsRecordAsync(int dnsRecordId, CancellationToken ct = default);

    /// <summary>Invoca <c>sp_GetUserDnsRecords</c>: lista los subdominios del usuario.</summary>
    Task<IReadOnlyList<DnsRecordInfo>> GetUserDnsRecordsAsync(
        int userId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_GetDnsRecordDetail</c>: trae un registro puntual validando
    /// que pertenezca a <paramref name="userId"/>. Si no hay fila es porque no
    /// existe o no es del usuario — mismo resultado a propósito, igual que en el
    /// catálogo de bases de datos (evita enumeración de IDs ajenos).
    /// </summary>
    Task<DnsRecordDetail?> GetDnsRecordDetailAsync(
        int dnsRecordId, int userId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_UpdateDnsRecord</c>: persiste la nueva IP de destino DESPUÉS
    /// de que el cambio ya se aplicó en el proveedor. El nombre del registro no
    /// se puede cambiar: eso sería otro subdominio, y se hace borrando y creando
    /// (así el catálogo conserva la historia de ambos).
    /// </summary>
    Task UpdateDnsRecordAsync(
        int dnsRecordId, int userId, string content, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_DeleteDnsRecord</c>: marca el registro como <c>'Deleted'</c>
    /// y registra <c>DeletedAt</c>. Se llama DESPUÉS de que el borrado en el
    /// proveedor ya se ejecutó con éxito. Al salir de los estados vivos, el FQDN
    /// queda libre para volver a pedirse.
    /// </summary>
    Task MarkDnsRecordDeletedAsync(
        int dnsRecordId, int userId, CancellationToken ct = default);

    // -----------------------------------------------------------------------
    // Administración (rol Admin) — sin filtro de propiedad
    // -----------------------------------------------------------------------

    /// <summary>
    /// Invoca <c>sp_GetAllDnsRecords</c>: listado de TODOS los registros de
    /// TODOS los usuarios, con filtros opcionales para auditoría. Todos los
    /// parámetros de filtro son nulables y el SP los trata como "sin filtrar"
    /// cuando vienen en <c>null</c>.
    ///
    /// <paramref name="minDaysSinceUpdate"/> es el filtro que sostiene el caso
    /// de uso de revocación por inactividad ("mostrame lo que nadie tocó en 90
    /// días"). Va acá y no como un post-filtrado en memoria porque el listado
    /// completo puede ser grande y filtrar después obligaría a traerlo entero.
    /// </summary>
    Task<IReadOnlyList<DnsRecordAdminInfo>> GetAllDnsRecordsAsync(
        string? cell, int? userId, string? status, int? minDaysSinceUpdate,
        CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_GetDnsRecordDetailAdmin</c>: detalle de cualquier registro,
    /// sin validar propiedad. Devuelve también los registros ya terminados
    /// (<c>Deleted</c>/<c>Revoked</c>), a diferencia de la versión del usuario:
    /// auditar es justamente poder mirar lo que ya no está vivo.
    /// </summary>
    Task<DnsRecordDetail?> GetDnsRecordDetailAdminAsync(
        int dnsRecordId, CancellationToken ct = default);

    /// <summary>
    /// Invoca <c>sp_RevokeDnsRecord</c>: marca el registro como <c>'Revoked'</c>
    /// dejando constancia de quién lo revocó y por qué. Se llama DESPUÉS de
    /// eliminarlo en el proveedor.
    ///
    /// Es un estado distinto de <c>'Deleted'</c> a propósito: los dos liberan el
    /// nombre, pero solo así una auditoría puede distinguir lo que el usuario
    /// dio de baja de lo que el equipo le quitó.
    /// </summary>
    Task RevokeDnsRecordAsync(
        int dnsRecordId, int revokedByUserId, string reason, CancellationToken ct = default);
}
