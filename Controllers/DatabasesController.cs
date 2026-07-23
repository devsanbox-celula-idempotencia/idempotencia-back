using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace idempotencia.Controllers;

/// <summary>
/// Endpoints de bases de datos aprovisionadas del usuario autenticado.
/// Todo requiere JWT válido. El controller media hacia el servicio de
/// aprovisionamiento (que orquesta SP de control + provisioner del motor) y
/// hacia el repositorio para las lecturas.
/// </summary>
[ApiController]
[Route("databases")]
[Authorize]
public class DatabasesController : ControllerBase
{
    private readonly IDatabaseProvisioningService _provisioning;
    private readonly IDatabaseRepository _databases;

    public DatabasesController(
        IDatabaseProvisioningService provisioning, IDatabaseRepository databases)
    {
        _provisioning = provisioning;
        _databases = databases;
    }

    /// <summary>Crea (aprovisiona) una BD del motor indicado para el usuario autenticado.</summary>
    [HttpPost]
    [EnableRateLimiting("db-provisioning")]
    public async Task<ActionResult<CreateDatabaseResponse>> Create(
        [FromBody] CreateDatabaseRequest request, CancellationToken ct)
    {
        var userId = GetUserId();

        var response = await _provisioning.ProvisionAsync(
            userId, request.Engine, request.DbName, request.MaxConcurrentConnections, ct);

        return CreatedAtAction(nameof(GetDetail), new { id = response.DatabaseId }, response);
    }

    /// <summary>Lista las BDs del usuario autenticado.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DatabaseResponse>>> GetMine(CancellationToken ct)
    {
        var userId = GetUserId();

        var items = await _databases.GetUserDatabasesAsync(userId, ct);

        var response = items.Select(MapToResponse);
        return Ok(response);
    }

    /// <summary>
    /// Detalle de una BD puntual — pensado para cuando el usuario perdió sus
    /// datos de conexión (host/puerto/usuario) y necesita volver a verlos.
    /// Nunca incluye la contraseña (no se puede recuperar; ver
    /// <see cref="ResetPassword"/>).
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<DatabaseDetailResponse>> GetDetail(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        var detail = await _provisioning.GetDetailAsync(userId, id, ct);
        return Ok(detail);
    }

    /// <summary>
    /// Revoca el acceso físico a la BD (login/usuario deshabilitado) sin
    /// borrar los datos. Paso obligatorio antes de poder eliminarla.
    /// </summary>
    [HttpPost("{id:int}/deactivate")]
    [EnableRateLimiting("db-provisioning")]
    public async Task<ActionResult<DatabaseDetailResponse>> Deactivate(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        var detail = await _provisioning.DeactivateAsync(userId, id, ct);
        return Ok(detail);
    }

    /// <summary>
    /// Elimina definitivamente la BD (borrado físico real) — solo permitido
    /// si ya está desactivada (<c>Status = "Inactive"</c>).
    /// </summary>
    [HttpDelete("{id:int}")]
    [EnableRateLimiting("db-provisioning")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        await _provisioning.DeleteAsync(userId, id, ct);
        return NoContent();
    }

    /// <summary>
    /// Genera una contraseña nueva para la BD, la aplica en el motor físico y
    /// la envía por correo al usuario autenticado — la respuesta HTTP nunca
    /// incluye la contraseña.
    /// </summary>
    [HttpPost("{id:int}/reset-password")]
    [EnableRateLimiting("db-provisioning")]
    public async Task<IActionResult> ResetPassword(int id, CancellationToken ct)
    {
        var userId = GetUserId();
        var (email, fullName) = GetUserContact();

        await _provisioning.ResetPasswordAsync(userId, id, email, fullName, ct);

        return Ok(new { status = 200, message = "Se envió la nueva contraseña a tu correo." });
    }

    // Extrae el UserId del claim del JWT emitido por el backend.
    private int GetUserId()
    {
        var raw = User.FindFirstValue("UserId");
        if (!int.TryParse(raw, out var userId))
            throw new AuthException("El token no contiene un identificador de usuario válido.");
        return userId;
    }

    // Extrae email/nombre del JWT para saber a dónde mandar el correo de
    // reset de contraseña. ClaimTypes.Email cubre el mapeo por defecto que
    // aplica JwtBearer al claim "email"; JwtRegisteredClaimNames.Email es el
    // fallback si esa mapeo estuviera deshabilitado (MapInboundClaims=false).
    private (string Email, string FullName) GetUserContact()
    {
        var email = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email);

        if (string.IsNullOrWhiteSpace(email))
            throw new AuthException("El token no contiene un correo válido.");

        // El JWT no lleva fullName (ver JwtTokenService) — se usa el correo
        // como nombre de respaldo para el saludo del correo si hiciera falta.
        var fullName = User.FindFirstValue(ClaimTypes.Name) ?? email;

        return (email, fullName);
    }

    private static DatabaseResponse MapToResponse(ProvisionedDatabaseInfo info) => new()
    {
        DatabaseId = info.DatabaseId,
        Engine = info.Engine,
        DbName = info.DbName,
        Status = info.Status,
        MaxStorageMB = info.MaxStorageMB,
        CurrentSizeMB = info.CurrentSizeMB,
        LastActivityAt = info.LastActivityAt,
        CreatedAt = info.CreatedAt,
        PausedAt = info.PausedAt
    };
}
