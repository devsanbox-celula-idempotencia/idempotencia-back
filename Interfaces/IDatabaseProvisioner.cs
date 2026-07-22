using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Estrategia de aprovisionamiento para UN motor concreto. Cada implementación
/// se conecta a su motor con el driver nativo y ejecuta la creación física de
/// la BD y su usuario. Es la ÚNICA parte que no puede vivir en un SP de SQL
/// Server (cada motor habla su propio protocolo).
/// </summary>
public interface IDatabaseProvisioner
{
    /// <summary>Motor que maneja esta implementación (ver <see cref="DatabaseEngine"/>).</summary>
    string Engine { get; }

    /// <summary>
    /// Crea físicamente la BD + usuario con permisos en el motor.
    /// <paramref name="maxConcurrentConnections"/> ya viene resuelto (default
    /// aplicado si el cliente no pidió uno, acotado al cap del motor) —
    /// los provisioners sin soporte nativo para esto simplemente lo ignoran.
    /// </summary>
    Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default);

    /// <summary>Elimina la BD + usuario (usado para revertir un aprovisionamiento fallido).</summary>
    Task DropAsync(string dbName, string login, CancellationToken ct = default);
}
