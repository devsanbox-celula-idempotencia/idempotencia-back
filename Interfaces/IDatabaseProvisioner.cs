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
    /// Host PÚBLICO que se le entrega al usuario para conectarse a sus BDs: sale
    /// de <c>Provisioning:IpVps</c> (ver <see cref="idempotencia.Services.ProvisioningSettings"/>),
    /// así que es el mismo para los cuatro motores y para todas las BDs del
    /// ambiente. No es el host con el que este provisioner habla con el motor
    /// (ese vive en su <c>AdminConnectionString</c> y en despliegue es el nombre
    /// del contenedor, que no resuelve desde afuera).
    /// </summary>
    string Host { get; }

    /// <summary>
    /// Puerto público de este motor (<c>Provisioning:{Engine}:Port</c>). A
    /// diferencia del host, sí es por motor: los cuatro conviven en la misma
    /// máquina y se distinguen por el puerto publicado.
    /// </summary>
    int Port { get; }

    /// <summary>
    /// Arma las cadenas de conexión que se le entregan al usuario final (en la
    /// respuesta de creación y en el correo de credenciales), con el parámetro
    /// de TLS propio de cada motor ya incluido cuando
    /// <c>Provisioning:{Engine}:RequireTls</c> está en <c>true</c>. La sintaxis
    /// de ese parámetro cambia por motor y por driver
    /// (<c>ssl-mode=REQUIRED</c> / <c>sslMode=REQUIRED</c> / <c>sslmode=require</c> /
    /// <c>tls=true</c> / <c>Encrypt=True</c>), que es justo lo que no se le
    /// puede pedir al usuario que adivine — ver
    /// <see cref="Models.ClientConnectionInfo"/> y docs/bugs.md ítem 28.
    ///
    /// No abre ninguna conexión: es construcción de strings, así que se puede
    /// llamar también para una BD ya existente (por ejemplo al resetear la
    /// contraseña, con la contraseña nueva).
    /// </summary>
    ClientConnectionInfo BuildClientConnection(string dbName, string login, string password);

    /// <summary>
    /// Crea físicamente la BD + usuario con permisos en el motor.
    /// <paramref name="maxConcurrentConnections"/> ya viene resuelto (default
    /// aplicado si el cliente no pidió uno, acotado al cap del motor) —
    /// los provisioners sin soporte nativo para esto simplemente lo ignoran.
    /// </summary>
    Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default);

    // -----------------------------------------------------------------------
    // Dos convenciones compartidas por los cuatro métodos de ciclo de vida que
    // vienen a continuación. Van como comentario y no como <summary> porque no
    // documentan un miembro concreto, sino el contrato entre todos ellos.
    //
    // externalId (Drop/ChangePassword/Deactivate/Reactivate): identificador con
    // el que un servicio EXTERNO de aprovisionamiento conoce la BD. Los
    // provisioners locales direccionan por nombre (DROP DATABASE x) y lo
    // ignoran; uno que delega en una API ajena no puede, porque esa API expone
    // sus recursos por id —no por nombre— y el nombre físico que ella genera ni
    // siquiera coincide con el que reservó el catálogo. Llega desde el catálogo
    // (sp_GetDatabaseExternalRef) y es null para toda base local.
    //
    // Retorno de las rotaciones de credencial: ChangePasswordAsync y
    // ReactivateAsync devuelven un CredentialRotationResult cuando la contraseña
    // que quedó vigente NO es la que se les pasó —caso de las APIs que generan
    // la suya— y null cuando sí aplicaron la recibida. El orquestador guarda el
    // hash y notifica al usuario en función de eso.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Elimina la BD + usuario. Usado tanto para revertir un aprovisionamiento
    /// fallido como para el borrado real de <c>DELETE /databases/{id}</c>
    /// (solo llamado ahí cuando la BD ya está Inactive) — es irreversible.
    /// </summary>
    Task DropAsync(string dbName, string login, string? externalId, CancellationToken ct = default);

    /// <summary>
    /// Cambia la contraseña del login/usuario físico sin tocar los datos ni
    /// los permisos existentes. Usado por <c>POST /databases/{id}/reset-password</c>.
    /// <paramref name="dbName"/> se recibe siempre (igual que en
    /// <see cref="DropAsync"/>) aunque solo Mongo lo necesita realmente —
    /// ahí el usuario está scoped a una BD concreta, no es un identificador a
    /// nivel de servidor como en los otros 3 motores.
    /// </summary>
    Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default);

    /// <summary>
    /// Revoca la capacidad de conexión del login/usuario SIN borrar la BD ni
    /// sus datos (reversible en principio, aunque hoy no existe un endpoint de
    /// "reactivar" — ver docs/bugs.md backlog). Usado por
    /// <c>POST /databases/{id}/deactivate</c>, paso previo obligatorio antes
    /// de poder eliminar la BD. Ver nota de <paramref name="dbName"/> en
    /// <see cref="ChangePasswordAsync"/>.
    /// </summary>
    Task DeactivateAsync(string dbName, string login, string? externalId, CancellationToken ct = default);

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
    Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default);

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
    ///
    /// Devuelve un valor NEGATIVO (por convención <c>-1</c>) cuando el motor no
    /// permite medir en absoluto —el caso de un provisioner que delega en una
    /// API externa que no expone tamaño por base—. No es lo mismo que <c>0</c>:
    /// cero afirma "está vacía" y se persistiría, mientras que el negativo dice
    /// "no lo sé", y el job lo trata como "conserva el último valor conocido" en
    /// vez de sobrescribir el catálogo con una mentira.
    /// </summary>
    Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default);
}
