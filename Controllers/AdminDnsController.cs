using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace idempotencia.Controllers;

/// <summary>
/// Control administrativo de los subdominios creados por los usuarios: auditar,
/// listar y revocar (por inactividad o abuso).
///
/// Va en un controller aparte de <see cref="DnsController"/> —y no como rutas
/// extra con un <c>[Authorize(Roles)]</c> encima— por dos motivos. El primero es
/// que la autorización queda declarada UNA vez a nivel de clase: no existe la
/// posibilidad de agregar mañana un endpoint acá y olvidarse del atributo, que
/// es el error que expone datos de todos los usuarios. El segundo es que el
/// contrato es distinto: estas respuestas incluyen a qué usuario pertenece cada
/// registro, un dato que en el controller del usuario no debe aparecer nunca.
///
/// Mismo patrón de autorización que <see cref="StatisticsController"/>: token
/// inválido/expirado → 401, usuario sin rol Admin → 403.
/// </summary>
[ApiController]
[Route("admin/dns")]
[Authorize(Roles = "Admin")]
public class AdminDnsController : ControllerBase
{
    private readonly IDnsProvisioningService _dns;

    public AdminDnsController(IDnsProvisioningService dns) => _dns = dns;

    /// <summary>
    /// Lista TODOS los subdominios de todos los usuarios, con filtros opcionales
    /// para auditoría. Sin filtros devuelve el inventario completo.
    /// </summary>
    /// <param name="cell">Filtra por célula (equipo de trabajo).</param>
    /// <param name="userId">Filtra por dueño.</param>
    /// <param name="status">
    /// Filtra por estado. Sin este filtro se devuelven solo los vivos
    /// (<c>Provisioning</c> y <c>Active</c>): el inventario histórico completo,
    /// con todo lo borrado y revocado, es la excepción y hay que pedirlo.
    /// </param>
    /// <param name="minDaysSinceUpdate">
    /// Solo registros sin modificaciones en al menos esta cantidad de días. Es
    /// el filtro que sostiene la revocación por inactividad: con
    /// <c>?minDaysSinceUpdate=90</c> sale la lista de candidatos.
    /// </param>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AdminDnsRecordResponse>>> GetAll(
        [FromQuery] string? cell,
        [FromQuery] int? userId,
        [FromQuery] string? status,
        [FromQuery] int? minDaysSinceUpdate,
        CancellationToken ct)
    {
        // Se valida el estado contra la lista conocida en vez de dejar que el SP
        // devuelva vacío ante un valor inexistente: un listado vacío es
        // indistinguible de "no hay registros en ese estado", y un operador
        // auditando puede concluir lo contrario de lo que pasa.
        if (status is not null && !IsKnownStatus(status))
        {
            throw new AppException(
                $"Estado no reconocido: '{status}'. Valores válidos: Provisioning, Active, Failed, Deleted, Revoked.",
                StatusCodes.Status400BadRequest);
        }

        if (minDaysSinceUpdate is < 0)
        {
            throw new AppException(
                "minDaysSinceUpdate no puede ser negativo.",
                StatusCodes.Status400BadRequest);
        }

        var items = await _dns.GetAllAsync(
            NormalizeCell(cell), userId, status, minDaysSinceUpdate, ct);

        return Ok(items);
    }

    /// <summary>
    /// Detalle de cualquier subdominio, sin filtro de propiedad. Incluye los ya
    /// terminados (<c>Deleted</c>/<c>Revoked</c>) — auditar es justamente poder
    /// mirar lo que ya no está vivo.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AdminDnsRecordResponse>> GetDetail(int id, CancellationToken ct)
    {
        var detail = await _dns.GetDetailAdminAsync(id, ct);
        return Ok(detail);
    }

    /// <summary>
    /// Revoca el subdominio de un usuario: lo elimina en el proveedor de DNS y
    /// lo marca como <c>Revoked</c> en el catálogo, con constancia de quién lo
    /// revocó y por qué.
    ///
    /// Es <c>POST</c> y no <c>DELETE</c> a propósito: la revocación lleva un
    /// cuerpo obligatorio (el motivo, que queda en el registro de auditoría), y
    /// un <c>DELETE</c> con cuerpo es algo que muchos proxies e intermediarios
    /// descartan en silencio.
    /// </summary>
    [HttpPost("{id:int}/revoke")]
    [EnableRateLimiting("dns")]
    public async Task<IActionResult> Revoke(
        int id, [FromBody] RevokeDnsRecordRequest request, CancellationToken ct)
    {
        var adminUserId = GetUserId();

        await _dns.RevokeAsync(adminUserId, id, request.Reason, ct);

        return Ok(new { status = 200, message = "Subdominio revocado." });
    }

    private static bool IsKnownStatus(string status) =>
        status.Equals(DnsRecordStatus.Provisioning, StringComparison.OrdinalIgnoreCase)
        || status.Equals(DnsRecordStatus.Active, StringComparison.OrdinalIgnoreCase)
        || status.Equals(DnsRecordStatus.Failed, StringComparison.OrdinalIgnoreCase)
        || status.Equals(DnsRecordStatus.Deleted, StringComparison.OrdinalIgnoreCase)
        || status.Equals(DnsRecordStatus.Revoked, StringComparison.OrdinalIgnoreCase);

    // Las células se guardan en minúsculas (el DTO de creación las normaliza),
    // así que el filtro tiene que normalizar igual o no encontraría nada cuando
    // el operador escribe "Datos" en la barra de direcciones.
    private static string? NormalizeCell(string? cell) =>
        string.IsNullOrWhiteSpace(cell) ? null : cell.Trim().ToLowerInvariant();

    // Mismo extractor que el resto de los controllers. Acá el UserId no se usa
    // para filtrar sino para dejar constancia de QUIÉN revocó.
    private int GetUserId()
    {
        var raw = User.FindFirstValue(JwtClaimNames.UserId);
        if (!int.TryParse(raw, out var userId))
            throw new AuthException("El token no contiene un identificador de usuario válido.");
        return userId;
    }
}
