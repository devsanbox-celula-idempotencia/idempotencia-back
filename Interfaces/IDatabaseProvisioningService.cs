using idempotencia.DTOs;

namespace idempotencia.Interfaces;

/// <summary>
/// Orquesta el aprovisionamiento multi-motor: reserva en el catálogo (SP),
/// crea físicamente en el motor (provisioner) y confirma o revierte. La lógica
/// de negocio (cuotas/límites) vive en el SP; aquí solo va la coordinación.
/// </summary>
public interface IDatabaseProvisioningService
{
    Task<CreateDatabaseResponse> ProvisionAsync(
        int userId, string engine, string dbName, CancellationToken ct = default);
}
