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

    /// <summary>
    /// Restaura la capacidad de conexión revocada por
    /// <see cref="DeactivateAsync"/>. Es la operación inversa exacta: cada
    /// motor deshace lo que hizo al desactivar (habilitar el login,
    /// desbloquear la cuenta, devolver el LOGIN al rol, restituir los roles del
    /// usuario). Usado por <c>POST /databases/{id}/reactivate</c>.
    ///
    /// Los datos nunca se tocaron al desactivar, así que reactivar devuelve la
    /// BD exactamente como estaba, con la misma contraseña. Es idempotente:
    /// aplicarlo sobre una BD que ya está habilitada es un no-op exitoso en los
    /// cuatro motores, lo que permite reintentar sin riesgo. Ver nota de
    /// <paramref name="dbName"/> en <see cref="ChangePasswordAsync"/>.
    /// </summary>
    Task ReactivateAsync(string dbName, string login, CancellationToken ct = default);

    /// <summary>
    /// Mide el tamaño real que ocupa la BD en el motor, en MB. Es la única
    /// fuente de verdad posible para <c>CurrentSizeMB</c>: el catálogo vive en
    /// SQL Server y no puede medir bases de MySQL/PostgreSQL/Mongo, así que la
    /// medición tiene que salir de acá, donde sí se habla el protocolo de cada
    /// motor. La consume <c>DatabaseSizeMonitor</c> — ver docs/bugs.md ítem 25.
    ///
    /// Cada motor reporta una noción de "tamaño" ligeramente distinta (espacio
    /// asignado en disco vs. bytes de datos e índices); cada implementación
    /// documenta cuál usa y por qué. Devuelve <c>0</c> si la BD ya no existe en
    /// el motor, en vez de lanzar: para el job una base desaparecida no es un
    /// error que deba abortar el ciclo.
    /// </summary>
    Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default);
}
