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
}
