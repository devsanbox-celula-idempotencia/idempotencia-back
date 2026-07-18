using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<ActionResult<CreateDatabaseResponse>> Create(
        [FromBody] CreateDatabaseRequest request, CancellationToken ct)
    {
        var userId = GetUserId();

        var response = await _provisioning.ProvisionAsync(userId, request.Engine, request.DbName, ct);

        return CreatedAtAction(nameof(GetMine), new { id = response.DatabaseId }, response);
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

    // Extrae el UserId del claim del JWT emitido por el backend.
    private int GetUserId()
    {
        var raw = User.FindFirstValue("UserId");
        if (!int.TryParse(raw, out var userId))
            throw new AuthException("El token no contiene un identificador de usuario válido.");
        return userId;
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
