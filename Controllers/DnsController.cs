using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace idempotencia.Controllers;

/// <summary>
/// Autoservicio de subdominios: el usuario pide, desde el panel, un subdominio
/// propio que apunte a su servicio o aplicación. La estructura es
///
/// <code>{nombre-elegido}.{celula}.{Dns:ZoneName}</code>
///
/// por ejemplo <c>airflow.datos.coderhivex.com</c>. Se crea un registro <b>A</b>
/// proxeado hacia la IPv4 que aporta el usuario; el HTTPS lo resuelve Cloudflare
/// automáticamente vía Total TLS (ver <see cref="DnsSettings.Proxied"/>).
///
/// Todo requiere JWT válido. Igual que <see cref="DatabasesController"/>, el
/// controller solo media: no conoce el orden en que hay que tocar catálogo y
/// proveedor externo ni cómo revertir — eso vive en
/// <see cref="IDnsProvisioningService"/>. La administración (auditar, revocar)
/// vive aparte, en <see cref="AdminDnsController"/>, porque tiene otro público y
/// otra autorización.
/// </summary>
[ApiController]
[Route("dns")]
[Authorize]
public class DnsController : ControllerBase
{
    private readonly IDnsProvisioningService _dns;
    private readonly DnsSettings _dnsSettings;

    public DnsController(IDnsProvisioningService dns, IOptions<DnsSettings> dnsSettings)
    {
        _dns = dns;
        _dnsSettings = dnsSettings.Value;
    }

    /// <summary>
    /// Crea un subdominio para el usuario autenticado y lo apunta a su IP.
    /// Devuelve el FQDN final ya armado, que es el dato que el usuario necesita.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("dns")]
    public async Task<ActionResult<DnsRecordResponse>> Create(
        [FromBody] CreateDnsRecordRequest request, CancellationToken ct)
    {
        var userId = GetUserId();

        var response = await _dns.CreateAsync(userId, request, ct);

        return CreatedAtAction(nameof(GetDetail), new { id = response.DnsRecordId }, response);
    }

    /// <summary>Lista los subdominios del usuario autenticado.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DnsRecordResponse>>> GetMine(CancellationToken ct)
    {
        var userId = GetUserId();
        var items = await _dns.GetMineAsync(userId, ct);
        return Ok(items);
    }

    /// <summary>
    /// Detalle de un subdominio puntual. 404 si no existe o si no pertenece al
    /// usuario — el mismo mensaje para ambos casos, a propósito, para no
    /// revelar la existencia de identificadores ajenos.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<DnsRecordResponse>> GetDetail(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        var detail = await _dns.GetDetailAsync(userId, id, ct);
        return Ok(detail);
    }

    /// <summary>
    /// Reapunta el subdominio a otra IP (por ejemplo, tras migrar de servidor).
    /// El nombre no se puede cambiar: eso sería otro subdominio, y se hace
    /// borrando y creando.
    /// </summary>
    [HttpPut("{id:int}")]
    [EnableRateLimiting("dns")]
    public async Task<ActionResult<DnsRecordResponse>> Update(
        int id, [FromBody] UpdateDnsRecordRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var detail = await _dns.UpdateAsync(userId, id, request, ct);
        return Ok(detail);
    }

    /// <summary>
    /// Elimina el subdominio. A diferencia de las bases de datos no exige un
    /// paso previo de desactivación: no se destruye ningún dato y el mismo
    /// nombre se puede volver a pedir después.
    /// </summary>
    [HttpDelete("{id:int}")]
    [EnableRateLimiting("dns")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        await _dns.DeleteAsync(userId, id, ct);
        return NoContent();
    }

    /// <summary>
    /// Informa la zona y el patrón de nombres. Existe para que el frontend pueda
    /// mostrar la vista previa del nombre completo mientras el usuario escribe,
    /// sin hardcodear el dominio (que cambia entre ambientes y quedaría
    /// desincronizado en silencio).
    /// </summary>
    [HttpGet("zone")]
    public ActionResult<object> GetZone() =>
        Ok(new
        {
            zoneName = _dnsSettings.ZoneName,
            defaultCell = _dnsSettings.DefaultCell,

            // Se devuelve el patrón ya resuelto con la célula por defecto —y no
            // la plantilla cruda— porque es lo que el frontend necesita para
            // mostrar la vista previa sin componer nada: basta con anteponerle
            // lo que el usuario está escribiendo.
            pattern = $"{{label}}.{_dnsSettings.DefaultCell}.{_dnsSettings.ZoneName}"
        });

    // Extrae el UserId del claim del JWT emitido por el backend. Es la misma
    // lógica que en DatabasesController: se repite (en vez de compartirse en una
    // clase base) porque son cuatro líneas sin estado y una clase base de
    // controllers acopla mucho más de lo que ahorra.
    private int GetUserId()
    {
        var raw = User.FindFirstValue(JwtClaimNames.UserId);
        if (!int.TryParse(raw, out var userId))
            throw new AuthException("El token no contiene un identificador de usuario válido.");
        return userId;
    }
}
