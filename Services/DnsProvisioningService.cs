using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using Microsoft.Extensions.Options;

namespace idempotencia.Services;

/// <summary>
/// Orquesta el ciclo de vida de los subdominios de autoservicio. Mismo patrón
/// que <see cref="DatabaseProvisioningService"/>: la lógica de negocio (cuota,
/// etiquetas reservadas, unicidad) vive en los SPs del catálogo y acá solo se
/// coordina el flujo reservar → crear en el proveedor → confirmar / revertir,
/// más el ciclo posterior (listar, detalle, reapuntar, eliminar) y las
/// operaciones de administración.
/// </summary>
public class DnsProvisioningService : IDnsProvisioningService
{
    private readonly IDnsRepository _repo;
    private readonly IDnsProvider _provider;
    private readonly DnsSettings _settings;
    private readonly ILogger<DnsProvisioningService> _logger;

    public DnsProvisioningService(
        IDnsRepository repo,
        IDnsProvider provider,
        IOptions<DnsSettings> settings,
        ILogger<DnsProvisioningService> logger)
    {
        _repo = repo;
        _provider = provider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DnsRecordResponse> CreateAsync(
        int userId, CreateDnsRecordRequest request, CancellationToken ct = default)
    {
        // El tipo de registro, el proxy y el TTL los fija la plataforma, no el
        // cliente: de ellos depende que el HTTPS funcione (ver DnsSettings.Proxied).
        // Lo único que el usuario aporta es el nombre, la célula y su IP.
        //
        // 1. Reserva en el catálogo. El SP valida cuota, formato, etiquetas
        //    reservadas y colisión, y devuelve el FQDN ya armado. Va ANTES de
        //    llamar a Cloudflare para que un usuario que excedió su cuota no
        //    llegue siquiera a gastar una llamada a la API externa, que tiene
        //    cuota compartida por toda la plataforma.
        // La célula es opcional en el request: hoy el despliegue tiene una sola
        // (Dns:DefaultCell) y hacer que el frontend la repita en cada llamada
        // sería obligarlo a hardcodear un valor que ya vive en la configuración
        // del backend. Lo que venga en el request gana, para que el día que haya
        // varias no haya que cambiar el contrato.
        var effectiveCell = string.IsNullOrWhiteSpace(request.Cell)
            ? _settings.DefaultCell
            : request.Cell;

        var reservation = await _repo.ReserveDnsRecordAsync(
            userId, request.Label, effectiveCell, _settings.ZoneName,
            _settings.RecordType, request.IpAddress,
            _settings.Proxied, _settings.TtlSeconds, ct);

        try
        {
            // 2. Creación real en el proveedor de DNS.
            var result = await _provider.CreateAsync(
                reservation.Fqdn, reservation.RecordType, reservation.Content,
                reservation.Proxied, reservation.Ttl, ct);

            // 3. Confirma en el catálogo y guarda el id del proveedor, que es lo
            //    que permite actualizar, borrar y revocar el registro después.
            await _repo.ConfirmDnsRecordAsync(reservation.DnsRecordId, result.ProviderRecordId, ct);

            _logger.LogInformation(
                "Subdominio {Fqdn} creado para el usuario {UserId} (célula {Cell}) -> {Ip}.",
                result.Fqdn, userId, reservation.Cell, reservation.Content);

            return new DnsRecordResponse
            {
                DnsRecordId = reservation.DnsRecordId,
                Label = reservation.Label,
                Cell = reservation.Cell,
                Fqdn = result.Fqdn,
                RecordType = reservation.RecordType,
                IpAddress = reservation.Content,
                Proxied = reservation.Proxied,
                Ttl = reservation.Ttl,
                Status = DnsRecordStatus.Active,
                CreatedAt = reservation.CreatedAt,
                UpdatedAt = reservation.CreatedAt
            };
        }
        catch (Exception ex)
        {
            // 4. Revertir. El orden importa: primero se intenta limpiar el
            //    proveedor y después se marca la reserva como fallida.
            //
            //    El caso que obliga a esto es el intermedio: el registro SÍ se
            //    creó en Cloudflare pero sp_ConfirmDnsRecord falló. Ahí el
            //    catálogo no tiene el ProviderRecordId (la confirmación es
            //    justamente la que lo guarda), así que el registro quedaría
            //    huérfano en la zona, resolviendo para siempre y bloqueando ese
            //    nombre sin que nadie pueda liberarlo desde la API. Por eso se
            //    busca por FQDN antes de rendirse.
            _logger.LogError(ex,
                "Fallo al crear el subdominio {Fqdn}; revirtiendo.", reservation.Fqdn);

            await TryCleanupProviderAsync(reservation.Fqdn, ct);

            try
            {
                await _repo.FailDnsRecordAsync(reservation.DnsRecordId, ct);
            }
            catch (Exception failEx)
            {
                _logger.LogError(failEx,
                    "Fallo marcando la reserva DNS {Id} como fallida.", reservation.DnsRecordId);
            }

            throw; // se propaga al middleware para la respuesta de error uniforme
        }
    }

    public async Task<IReadOnlyList<DnsRecordResponse>> GetMineAsync(
        int userId, CancellationToken ct = default)
    {
        var items = await _repo.GetUserDnsRecordsAsync(userId, ct);
        return items.Select(info => MapToResponse(info)).ToList();
    }

    public async Task<DnsRecordResponse> GetDetailAsync(
        int userId, int dnsRecordId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDnsRecordDetailAsync(dnsRecordId, userId, ct)
            ?? throw new NotFoundException("Subdominio no encontrado.");

        return MapToResponse(detail);
    }

    public async Task<DnsRecordResponse> UpdateAsync(
        int userId, int dnsRecordId, UpdateDnsRecordRequest request,
        CancellationToken ct = default)
    {
        var detail = await _repo.GetDnsRecordDetailAsync(dnsRecordId, userId, ct)
            ?? throw new NotFoundException("Subdominio no encontrado.");

        if (!string.Equals(detail.Status, DnsRecordStatus.Active, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                "Solo se puede reapuntar un subdominio activo.",
                StatusCodes.Status400BadRequest);
        }

        var providerRecordId = await ResolveProviderRecordIdAsync(detail, ct)
            ?? throw new NotFoundException(
                "El registro ya no existe en el proveedor de DNS. Elimínalo y vuelve a crearlo.");

        // Proveedor PRIMERO, catálogo después — mismo criterio que las bases de
        // datos: si esto se invierte y el proveedor falla, el catálogo quedaría
        // prometiendo un destino que el DNS no resuelve, que es la
        // desincronización que el usuario no puede detectar ni corregir.
        //
        // El proxy y el TTL se reenvían con el valor de configuración y no con el
        // guardado en la fila: si alguien los cambió a mano en el panel de
        // Cloudflare, esta operación los devuelve a lo que la plataforma
        // garantiza (y de lo que depende el certificado).
        await _provider.UpdateAsync(
            providerRecordId, detail.Fqdn, detail.RecordType, request.IpAddress,
            _settings.Proxied, _settings.TtlSeconds, ct);

        await _repo.UpdateDnsRecordAsync(dnsRecordId, userId, request.IpAddress, ct);

        detail.Content = request.IpAddress;
        detail.Proxied = _settings.Proxied;
        detail.Ttl = _settings.TtlSeconds;
        detail.UpdatedAt = DateTime.UtcNow;

        return MapToResponse(detail);
    }

    public async Task DeleteAsync(int userId, int dnsRecordId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDnsRecordDetailAsync(dnsRecordId, userId, ct)
            ?? throw new NotFoundException("Subdominio no encontrado.");

        // A diferencia de las bases de datos, borrar acá no exige desactivar
        // primero: no se destruye ningún dato del usuario y el mismo subdominio
        // se puede volver a crear con el mismo nombre. Exigir un paso previo
        // sería fricción sin nada que proteger.
        await RemoveFromProviderAsync(detail, ct);
        await _repo.MarkDnsRecordDeletedAsync(dnsRecordId, userId, ct);
    }

    // -----------------------------------------------------------------------
    // Administración
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<AdminDnsRecordResponse>> GetAllAsync(
        string? cell, int? userId, string? status, int? minDaysSinceUpdate,
        CancellationToken ct = default)
    {
        var items = await _repo.GetAllDnsRecordsAsync(cell, userId, status, minDaysSinceUpdate, ct);
        return items.Select(MapToAdminResponse).ToList();
    }

    public async Task<AdminDnsRecordResponse> GetDetailAdminAsync(
        int dnsRecordId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDnsRecordDetailAdminAsync(dnsRecordId, ct)
            ?? throw new NotFoundException("Subdominio no encontrado.");

        var response = MapToResponse(detail);

        // El detalle administrativo reusa el SP de detalle, que no hace JOIN con
        // usuarios: trae el UserId pero no el correo. Se devuelve lo que hay en
        // vez de agregar un JOIN a un SP que también usa el flujo del usuario,
        // donde ese dato no aporta nada. El correo sí viene en el listado.
        return new AdminDnsRecordResponse
        {
            DnsRecordId = response.DnsRecordId,
            Label = response.Label,
            Cell = response.Cell,
            Fqdn = response.Fqdn,
            RecordType = response.RecordType,
            IpAddress = response.IpAddress,
            Proxied = response.Proxied,
            Ttl = response.Ttl,
            Status = response.Status,
            CreatedAt = response.CreatedAt,
            UpdatedAt = response.UpdatedAt,
            DeletedAt = response.DeletedAt,
            UserId = detail.UserId,
            DaysSinceUpdate = (int)(DateTime.UtcNow - detail.UpdatedAt).TotalDays
        };
    }

    public async Task RevokeAsync(
        int adminUserId, int dnsRecordId, string reason, CancellationToken ct = default)
    {
        var detail = await _repo.GetDnsRecordDetailAdminAsync(dnsRecordId, ct)
            ?? throw new NotFoundException("Subdominio no encontrado.");

        if (detail.Status is DnsRecordStatus.Deleted or DnsRecordStatus.Revoked)
        {
            throw new AppException(
                "Ese subdominio ya no está activo.",
                StatusCodes.Status400BadRequest);
        }

        await RemoveFromProviderAsync(detail, ct);
        await _repo.RevokeDnsRecordAsync(dnsRecordId, adminUserId, reason, ct);

        // Se loguea con nivel Warning y no Information a propósito: revocar el
        // subdominio de otra persona es una acción administrativa con impacto
        // directo sobre un servicio ajeno, y tiene que ser fácil de encontrar en
        // los logs sin filtrar entre el ruido del uso normal.
        _logger.LogWarning(
            "El admin {AdminUserId} revocó el subdominio {Fqdn} del usuario {UserId}. Motivo: {Reason}",
            adminUserId, detail.Fqdn, detail.UserId, reason);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Elimina el registro en el proveedor si existe. Compartido por el borrado
    /// del usuario y la revocación administrativa, que solo se diferencian en
    /// qué escriben después en el catálogo.
    /// </summary>
    private async Task RemoveFromProviderAsync(DnsRecordDetail detail, CancellationToken ct)
    {
        var providerRecordId = await ResolveProviderRecordIdAsync(detail, ct);

        if (providerRecordId is not null)
        {
            // DeleteAsync del proveedor es idempotente ante un registro
            // inexistente, así que reintentar un borrado a medias es seguro.
            await _provider.DeleteAsync(providerRecordId, ct);
            return;
        }

        // No está en el proveedor: la fila quedó en Provisioning/Failed sin
        // registro asociado, o alguien lo borró a mano desde el panel. El estado
        // buscado ya se cumple, así que se sigue y se limpia el catálogo en vez
        // de dejar la fila atascada para siempre.
        _logger.LogInformation(
            "El subdominio {Fqdn} no existe en {Provider}; solo se limpia el catálogo.",
            detail.Fqdn, _provider.Provider);
    }

    /// <summary>
    /// Devuelve el id del registro en el proveedor, prefiriendo el que ya está
    /// en el catálogo y cayendo a una búsqueda por FQDN si falta. Ese segundo
    /// camino es el que permite recuperar un registro huérfano (creado en el
    /// proveedor pero nunca confirmado en el catálogo).
    /// </summary>
    private async Task<string?> ResolveProviderRecordIdAsync(
        DnsRecordDetail detail, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(detail.ProviderRecordId))
            return detail.ProviderRecordId;

        return await _provider.FindRecordIdAsync(detail.Fqdn, ct);
    }

    /// <summary>
    /// Limpieza best-effort del proveedor tras un fallo de creación. Nunca
    /// propaga: se la llama desde un <c>catch</c> que ya tiene una excepción en
    /// vuelo, y perder la causa original por un fallo de la limpieza haría el
    /// error mucho más difícil de diagnosticar.
    /// </summary>
    private async Task TryCleanupProviderAsync(string fqdn, CancellationToken ct)
    {
        try
        {
            var orphanId = await _provider.FindRecordIdAsync(fqdn, ct);
            if (orphanId is not null)
                await _provider.DeleteAsync(orphanId, ct);
        }
        catch (Exception cleanupEx)
        {
            _logger.LogError(cleanupEx,
                "Fallo limpiando el registro DNS {Fqdn} tras error.", fqdn);
        }
    }

    private static DnsRecordResponse MapToResponse(DnsRecordInfo info) => new()
    {
        DnsRecordId = info.DnsRecordId,
        Label = info.Label,
        Cell = info.Cell,
        Fqdn = info.Fqdn,
        RecordType = info.RecordType,
        IpAddress = info.Content,
        Proxied = info.Proxied,
        Ttl = info.Ttl,
        Status = info.Status,
        CreatedAt = info.CreatedAt,
        UpdatedAt = info.UpdatedAt,
        DeletedAt = info.DeletedAt
    };

    private static DnsRecordResponse MapToResponse(DnsRecordDetail detail) => new()
    {
        DnsRecordId = detail.DnsRecordId,
        Label = detail.Label,
        Cell = detail.Cell,
        Fqdn = detail.Fqdn,
        RecordType = detail.RecordType,
        IpAddress = detail.Content,
        Proxied = detail.Proxied,
        Ttl = detail.Ttl,
        Status = detail.Status,
        CreatedAt = detail.CreatedAt,
        UpdatedAt = detail.UpdatedAt,
        DeletedAt = detail.DeletedAt
    };

    private static AdminDnsRecordResponse MapToAdminResponse(DnsRecordAdminInfo info) => new()
    {
        DnsRecordId = info.DnsRecordId,
        Label = info.Label,
        Cell = info.Cell,
        Fqdn = info.Fqdn,
        RecordType = info.RecordType,
        IpAddress = info.Content,
        Proxied = info.Proxied,
        Ttl = info.Ttl,
        Status = info.Status,
        CreatedAt = info.CreatedAt,
        UpdatedAt = info.UpdatedAt,
        DeletedAt = info.DeletedAt,
        UserId = info.UserId,
        UserEmail = info.UserEmail,
        DaysSinceUpdate = info.DaysSinceUpdate
    };
}
