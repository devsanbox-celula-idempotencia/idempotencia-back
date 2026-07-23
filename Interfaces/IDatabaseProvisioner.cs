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

    /// <summary>Host de conexión de este motor (mismo para todas las BDs de este motor/ambiente).</summary>
    string Host { get; }

    /// <summary>Puerto de conexión de este motor.</summary>
    int Port { get; }

    /// <summary>
    /// Crea físicamente la BD + usuario con permisos en el motor.
    /// <paramref name="maxConcurrentConnections"/> ya viene resuelto (default
    /// aplicado si el cliente no pidió uno, acotado al cap del motor) —
    /// los provisioners sin soporte nativo para esto simplemente lo ignoran.
    /// </summary>
    Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default);

    /// <summary>
    /// Elimina la BD + usuario. Usado tanto para revertir un aprovisionamiento
    /// fallido como para el borrado real de <c>DELETE /databases/{id}</c>
    /// (solo llamado ahí cuando la BD ya está Inactive) — es irreversible.
    /// </summary>
    Task DropAsync(string dbName, string login, CancellationToken ct = default);

    /// <summary>
    /// Cambia la contraseña del login/usuario físico sin tocar los datos ni
    /// los permisos existentes. Usado por <c>POST /databases/{id}/reset-password</c>.
    /// <paramref name="dbName"/> se recibe siempre (igual que en
    /// <see cref="DropAsync"/>) aunque solo Mongo lo necesita realmente —
    /// ahí el usuario está scoped a una BD concreta, no es un identificador a
    /// nivel de servidor como en los otros 3 motores.
    /// </summary>
    Task ChangePasswordAsync(string dbName, string login, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Revoca la capacidad de conexión del login/usuario SIN borrar la BD ni
    /// sus datos (reversible en principio, aunque hoy no existe un endpoint de
    /// "reactivar" — ver docs/bugs.md backlog). Usado por
    /// <c>POST /databases/{id}/deactivate</c>, paso previo obligatorio antes
    /// de poder eliminar la BD. Ver nota de <paramref name="dbName"/> en
    /// <see cref="ChangePasswordAsync"/>.
    /// </summary>
    Task DeactivateAsync(string dbName, string login, CancellationToken ct = default);
}
