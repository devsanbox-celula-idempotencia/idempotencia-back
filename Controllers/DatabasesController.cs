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
/// Todo requiere JWT válido. El controller solo media hacia el repositorio,
/// que invoca los SPs; las validaciones (cuota, límites) viven en el SP.
/// </summary>
[ApiController]
[Route("databases")]
[Authorize]
public class DatabasesController : ControllerBase
{
    private readonly IDatabaseRepository _databases;

    public DatabasesController(IDatabaseRepository databases) => _databases = databases;

    /// <summary>Crea (aprovisiona) una BD para el usuario autenticado.</summary>
    [HttpPost]
    public async Task<ActionResult<CreateDatabaseResponse>> Create(
        [FromBody] CreateDatabaseRequest request, CancellationToken ct)
    {
        var userId = GetUserId();

        var result = await _databases.CreateDatabaseAsync(userId, request.DbName, ct);

        var response = new CreateDatabaseResponse
        {
            DatabaseId = result.DatabaseId,
            DbName = result.DbName,
            Status = result.Status,
            MaxStorageMB = result.MaxStorageMB,
            LoginName = result.LoginName,
            Password = result.Password
        };

        return CreatedAtAction(nameof(GetMine), new { id = result.DatabaseId }, response);
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
        DbName = info.DbName,
        Status = info.Status,
        MaxStorageMB = info.MaxStorageMB,
        CurrentSizeMB = info.CurrentSizeMB,
        LastActivityAt = info.LastActivityAt,
        CreatedAt = info.CreatedAt,
        PausedAt = info.PausedAt
    };
}
