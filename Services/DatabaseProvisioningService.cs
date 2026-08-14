using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Services;

/// <summary>
/// Orquesta el aprovisionamiento multi-motor. Coordina el catálogo (SPs de
/// control) con el provisioner físico del motor. La lógica de negocio (cuotas,
/// límites, nombres) vive en el SP <c>sp_ReserveDatabase</c>; aquí solo se
/// coordina el flujo reservar → crear → confirmar / revertir. También
/// orquesta el ciclo de vida posterior (detalle, desactivar, eliminar, reset
/// de contraseña).
///
/// <b>Motores locales vs. delegados.</b> Desde que MongoDB y MySQL se
/// aprovisionan contra APIs externas (ver
/// <see cref="idempotencia.Provisioners.RemoteMongoProvisioner"/> y
/// <see cref="idempotencia.Provisioners.RemoteMySqlProvisioner"/>),
/// este servicio ya no puede asumir dos cosas que antes eran obvias: que la BD
/// quedó creada con el nombre, el usuario y la contraseña que él decidió, y que
/// basta el nombre para volver a operarla. De ahí salen las dos piezas nuevas del flujo
/// —quedarse con los valores EFECTIVOS que reporta el provisioner, y persistir
/// y releer la referencia externa— que para los cuatro motores locales son
/// no-ops, porque ahí el provisioner no reporta nada distinto.
/// </summary>
public class DatabaseProvisioningService : IDatabaseProvisioningService
{
    private readonly IDatabaseRepository _repo;
    private readonly IDatabaseProvisionerFactory _factory;
    private readonly IConfiguration _config;
    private readonly IEmailService _email;
    private readonly ILogger<DatabaseProvisioningService> _logger;

    public DatabaseProvisioningService(
        IDatabaseRepository repo,
        IDatabaseProvisionerFactory factory,
        IConfiguration config,
        IEmailService email,
        ILogger<DatabaseProvisioningService> logger)
    {
        _repo = repo;
        _factory = factory;
        _config = config;
        _email = email;
        _logger = logger;
    }

    public async Task<CreateDatabaseResponse> ProvisionAsync(
        int userId, string engine, string dbName,
        int? requestedMaxConcurrentConnections = null, CancellationToken ct = default)
    {
        // 1. Valida que el motor esté soportado (400 si no) ANTES de tocar la DB.
        var provisioner = _factory.Get(engine);

        // 1b. Resuelve el tope de conexiones concurrentes: si el cliente pidió
        // uno, se acota SIEMPRE al cap del motor (nunca se confía en lo que
        // pide el cliente sin límite); si no pidió nada, se usa el default del
        // motor. En motores sin soporte nativo (SqlServer/Mongo) el provisioner
        // simplemente ignora este valor.
        var effectiveMaxConcurrentConnections =
            ResolveMaxConcurrentConnections(engine, requestedMaxConcurrentConnections);

        // 2. Reserva en el catálogo: el SP valida cuota/límites y genera nombres.
        var reservation = await _repo.ReserveDatabaseAsync(userId, engine, dbName, ct);

        // 3. Contraseña generada en el backend (solo se devuelve una vez). En los
        // motores delegados en una API externa es solo una propuesta: si esa API
        // genera la suya, la que vale es la que ella devuelve.
        var password = PasswordGenerator.Generate();

        // Declarado FUERA del try para que la reversión del catch lo vea. Si la
        // creación externa llegó a completarse y lo que falló fue el paso
        // siguiente, este id es lo único con lo que se puede borrar la base que
        // quedó viva del otro lado.
        string? externalId = null;

        try
        {
            // 4. Creación física en el motor correspondiente.
            var result = await provisioner.CreateAsync(
                reservation.DbName, reservation.LoginName, password, reservation.MaxStorageMB,
                effectiveMaxConcurrentConnections, ct);

            externalId = result.ExternalId;

            // 4b. Valores EFECTIVOS: lo que realmente quedó creado, que no
            // siempre es lo que se pidió. Los provisioners locales los devuelven
            // todos en null y esto se resuelve a la reserva de siempre.
            var effectiveDbName = result.EffectiveDbName ?? reservation.DbName;
            var effectiveLogin = result.EffectiveLogin ?? reservation.LoginName;
            var effectivePassword = result.EffectivePassword ?? password;

            // La cuota que se le reporta al usuario es la que REALMENTE lo va a
            // frenar. Cuando la fija el servicio externo (MySQL socio: 20 MB), la
            // del catálogo es un número que no gobierna nada y mostrarlo haría que
            // su base se bloquee sin explicación.
            var effectiveMaxStorageMb = result.ExternalMaxStorageMB ?? reservation.MaxStorageMB;

            // 4c. Persistir la referencia externa ANTES de confirmar. El orden
            // importa: si esta escritura falla, el catch de abajo todavía puede
            // borrar la base en la API (tiene el id en memoria) y revertir la
            // reserva. Al revés —confirmar primero— una falla acá dejaría una
            // base confirmada y viva, imposible de eliminar desde Colmena
            // porque nadie sabría su id.
            if (externalId is not null)
            {
                await _repo.SetDatabaseExternalRefAsync(
                    reservation.DatabaseId, externalId, result.EffectiveDbName,
                    result.EffectiveLogin, result.ExternalMaxStorageMB, ct);
            }

            // 5. Confirma en el catálogo y guarda el HASH de la contraseña que
            // quedó realmente vigente (no la propuesta, que en los motores
            // delegados se descarta).
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(effectivePassword);
            await _repo.ConfirmDatabaseAsync(reservation.DatabaseId, passwordHash, ct);

            // Cadenas de conexión ya armadas para el usuario (con el parámetro de
            // TLS del motor incluido). Es solo construcción de strings sobre datos
            // que ya tenemos, así que va después de confirmar: si algo falló antes,
            // no hay conexión que entregar. Ver docs/bugs.md ítem 28.
            var clientConn = provisioner.BuildClientConnection(
                effectiveDbName, effectiveLogin, effectivePassword);

            return new CreateDatabaseResponse
            {
                DatabaseId = reservation.DatabaseId,
                Engine = engine,
                // Se reporta el nombre EFECTIVO y no el del catálogo: es al que
                // el usuario tiene que conectarse. Cuando difieren, el del
                // catálogo es un identificador interno que no le sirve de nada.
                DbName = effectiveDbName,
                Status = "Active",
                MaxStorageMB = effectiveMaxStorageMb,
                MaxConcurrentConnections = effectiveMaxConcurrentConnections,
                Host = result.Host,
                Port = result.Port,
                // Mismo criterio que DbName: el usuario que se reporta es con el
                // que el estudiante realmente se autentica, no el que reservó el
                // catálogo. La API socia de MySQL genera el suyo con su prefijo.
                LoginName = effectiveLogin,
                Password = effectivePassword,
                // Se prefiere la cadena que devuelve el propio servicio que creó
                // la base: es la única correcta por construcción.
                // BuildClientConnection es la reconstrucción de respaldo.
                ConnectionUri = result.ConnectionUri ?? clientConn.Uri,
                JdbcUrl = clientConn.JdbcUrl
            };
        }
        catch (Exception ex)
        {
            // 6. Revertir: intentar limpiar el motor y marcar la reserva como fallida.
            _logger.LogError(ex, "Fallo al aprovisionar la BD {Db} ({Engine}); revirtiendo.",
                reservation.DbName, engine);

            try
            {
                await provisioner.DropAsync(reservation.DbName, reservation.LoginName, externalId, ct);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Fallo limpiando la BD {Db} tras error.", reservation.DbName);
            }

            try
            {
                await _repo.FailDatabaseAsync(reservation.DatabaseId, ct);
            }
            catch (Exception failEx)
            {
                _logger.LogError(failEx, "Fallo marcando la reserva {Id} como fallida.",
                    reservation.DatabaseId);
            }

            throw; // se propaga al middleware para la respuesta de error uniforme
        }
    }

    public async Task<DatabaseDetailResponse> GetDetailAsync(
        int userId, int databaseId, CancellationToken ct = default)
    {
        var (detail, externalRef) = await LoadAsync(userId, databaseId, ct);

        var provisioner = _factory.Get(detail.Engine);
        return MapToDetailResponse(detail, provisioner, externalRef);
    }

    public async Task<DatabaseDetailResponse> DeactivateAsync(
        int userId, int databaseId, CancellationToken ct = default)
    {
        var (detail, externalRef) = await LoadAsync(userId, databaseId, ct);

        if (!string.Equals(detail.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "Solo se puede desactivar una base de datos que esté activa.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);

        // Revoca el acceso físico PRIMERO; si esto falla, el catálogo sigue
        // reflejando la realidad (la BD sigue Active) en vez de quedar
        // desincronizado.
        await provisioner.DeactivateAsync(
            PhysicalName(detail, externalRef), PhysicalLogin(detail, externalRef),
            externalRef?.ExternalId, ct);

        await _repo.DeactivateDatabaseAsync(databaseId, userId, ct);

        detail.Status = "Inactive";
        detail.PausedAt = DateTime.UtcNow;
        return MapToDetailResponse(detail, provisioner, externalRef);
    }

    public async Task<DatabaseDetailResponse> ReactivateAsync(
        int userId, int databaseId, string userEmail, string userFullName,
        CancellationToken ct = default)
    {
        var (detail, externalRef) = await LoadAsync(userId, databaseId, ct);

        if (!string.Equals(detail.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "Solo se puede reactivar una base de datos que esté inactiva.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);

        // Mismo orden que DeactivateAsync — motor primero, catálogo después —
        // pero por una razón distinta, que conviene dejar explícita porque el
        // razonamiento de allá no aplica tal cual acá.
        //
        // Al desactivar, ese orden protege contra "el catálogo dice Inactive
        // pero el usuario todavía puede conectarse". Al reactivar, protege
        // contra el desbalance contrario: si el catálogo dijera 'Active' y el
        // motor hubiera fallado, el usuario vería su BD como disponible sin
        // poder conectarse, y la UI ni siquiera le ofrecería el botón de
        // reactivar para reintentar (la guarda de arriba exige 'Inactive'), así
        // que quedaría atascado.
        //
        // Con este orden, si falla el catálogo lo que queda es una BD
        // físicamente habilitada pero marcada 'Inactive': el usuario todavía ve
        // el botón, vuelve a pulsarlo, y como ReactivateAsync es idempotente en
        // los cuatro motores locales el reintento se completa limpio. Es el
        // estado desincronizado recuperable de los dos.
        var rotated = await provisioner.ReactivateAsync(
            PhysicalName(detail, externalRef), PhysicalLogin(detail, externalRef),
            externalRef?.ExternalId, ct);

        await _repo.ReactivateDatabaseAsync(databaseId, userId, ct);

        detail.Status = "Active";
        detail.PausedAt = null;

        // Motores que no pueden devolver el acceso con la contraseña de siempre
        // (hoy solo Mongo, por la API externa) emiten una nueva al reactivar.
        // Hay que persistir su hash y hacérsela llegar al usuario, o quedaría
        // con una BD "Active" a la que no puede entrar.
        if (rotated is not null)
        {
            await PersistAndNotifyRotationAsync(
                userId, detail, provisioner, rotated, externalRef, userEmail, userFullName,
                subject: $"Colmena — tu base de datos {PhysicalName(detail, externalRef)} volvió a estar activa",
                buildBody: EmailTemplates.DatabaseReactivatedCredentials,
                ct: ct);
        }

        return MapToDetailResponse(detail, provisioner, externalRef);
    }

    public async Task DeleteAsync(int userId, int databaseId, CancellationToken ct = default)
    {
        var (detail, externalRef) = await LoadAsync(userId, databaseId, ct);

        if (!string.Equals(detail.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "La base de datos debe estar inactiva antes de poder eliminarla. " +
                "Desactívala primero con POST /databases/{id}/deactivate.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);

        // Borrado físico real (irreversible). Se marca en el catálogo recién
        // después de que el motor confirme el borrado.
        await provisioner.DropAsync(
            PhysicalName(detail, externalRef), PhysicalLogin(detail, externalRef),
            externalRef?.ExternalId, ct);

        await _repo.MarkDatabaseDeletedAsync(databaseId, userId, ct);
    }

    public async Task ResetPasswordAsync(
        int userId, int databaseId, string userEmail, string userFullName, CancellationToken ct = default)
    {
        var (detail, externalRef) = await LoadAsync(userId, databaseId, ct);

        if (!string.Equals(detail.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "Solo se puede restablecer la contraseña de una base de datos activa.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);
        var newPassword = PasswordGenerator.Generate();

        // Cambia la contraseña física PRIMERO; si falla, la contraseña
        // anterior sigue siendo la válida y no se toca el catálogo.
        var rotated = await provisioner.ChangePasswordAsync(
            PhysicalName(detail, externalRef), PhysicalLogin(detail, externalRef), newPassword,
            externalRef?.ExternalId, ct);

        // rotated no nulo = el motor impuso su propia contraseña (API externa) y
        // la que se generó arriba nunca existió. Guardar el hash de la propuesta
        // dejaría al usuario con una credencial que no valida jamás.
        await PersistAndNotifyRotationAsync(
            userId, detail, provisioner,
            rotated ?? new CredentialRotationResult(newPassword, null),
            externalRef, userEmail, userFullName,
            subject: $"Colmena — nueva contraseña para {PhysicalName(detail, externalRef)}",
            buildBody: EmailTemplates.DatabasePasswordReset,
            ct: ct);
    }

    /// <summary>
    /// Guarda el hash de una contraseña recién rotada y se la envía al usuario.
    /// Compartido por el reset explícito y por la reactivación de los motores
    /// que no pueden devolver el acceso con la contraseña anterior.
    ///
    /// El correo es la ÚNICA forma en que el usuario recibe esta contraseña — a
    /// propósito no se devuelve en la respuesta HTTP (no queda en logs de
    /// acceso/historial del navegador). Si el envío falla, SÍ se propaga la
    /// excepción (mapea a 500 genérico): la contraseña física ya cambió, así que
    /// el usuario debe enterarse de que algo salió mal para poder reintentar o
    /// contactar soporte, en vez de quedarse bloqueado sin saberlo.
    /// </summary>
    private async Task PersistAndNotifyRotationAsync(
        int userId,
        ProvisionedDatabaseDetail detail,
        IDatabaseProvisioner provisioner,
        CredentialRotationResult rotated,
        ExternalDatabaseRef? externalRef,
        string userEmail,
        string userFullName,
        string subject,
        Func<ProvisionedDatabaseDetail, string, ClientConnectionInfo, string> buildBody,
        CancellationToken ct)
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(rotated.Password);
        await _repo.ResetDatabasePasswordAsync(detail.DatabaseId, userId, passwordHash, ct);

        // Igual que al crear: si el servicio externo devolvió su propia cadena,
        // esa manda sobre la que reconstruye el provisioner.
        var built = provisioner.BuildClientConnection(
            PhysicalName(detail, externalRef), PhysicalLogin(detail, externalRef), rotated.Password);

        var conn = rotated.ConnectionUri is null
            ? built
            : new ClientConnectionInfo(rotated.ConnectionUri, built.JdbcUrl);

        await _email.SendAsync(
            userEmail, userFullName, subject, buildBody(detail, rotated.Password, conn), ct);
    }

    /// <summary>
    /// Lee el detalle de la BD y, en la misma operación, su referencia externa.
    /// Todos los flujos del ciclo de vida la necesitan, así que se cargan
    /// juntas en vez de repetir el par de llamadas en cada método.
    /// </summary>
    private async Task<(ProvisionedDatabaseDetail Detail, ExternalDatabaseRef? ExternalRef)> LoadAsync(
        int userId, int databaseId, CancellationToken ct)
    {
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

        var externalRef = await _repo.GetDatabaseExternalRefAsync(databaseId, userId, ct);

        return (detail, externalRef);
    }

    /// <summary>
    /// Nombre con el que el provisioner tiene que dirigirse a la BD en su motor.
    /// Para los cuatro motores locales es el del catálogo; para una BD creada
    /// por un servicio externo es el que ESE servicio generó, que no coincide.
    /// Usar el del catálogo ahí apuntaría a una base que no existe.
    /// </summary>
    private static string PhysicalName(
        ProvisionedDatabaseDetail detail, ExternalDatabaseRef? externalRef) =>
        string.IsNullOrWhiteSpace(externalRef?.ExternalDbName)
            ? detail.DbName
            : externalRef.ExternalDbName!;

    /// <summary>
    /// Usuario con el que el provisioner tiene que dirigirse al motor, y con el
    /// que el estudiante se autentica. Análogo de <see cref="PhysicalName"/>: en
    /// los motores locales y en Mongo es el del catálogo (esos servicios aceptan
    /// el nombre que se les manda); en la API socia de MySQL no, porque su
    /// endpoint de creación no lleva cuerpo y el usuario lo genera ella.
    /// </summary>
    private static string PhysicalLogin(
        ProvisionedDatabaseDetail detail, ExternalDatabaseRef? externalRef) =>
        string.IsNullOrWhiteSpace(externalRef?.ExternalLoginName)
            ? detail.LoginName
            : externalRef.ExternalLoginName!;

    private static DatabaseDetailResponse MapToDetailResponse(
        ProvisionedDatabaseDetail detail, IDatabaseProvisioner provisioner,
        ExternalDatabaseRef? externalRef) => new()
    {
        DatabaseId = detail.DatabaseId,
        Engine = detail.Engine,
        // Mismo criterio que en la creación: se muestra el nombre al que el
        // usuario realmente se conecta, no el identificador interno del catálogo.
        DbName = PhysicalName(detail, externalRef),
        Status = detail.Status,
        Host = provisioner.Host,
        Port = provisioner.Port,
        LoginName = PhysicalLogin(detail, externalRef),
        // Igual que al crear: manda la cuota que realmente aplica el servicio
        // que hospeda la base, no la que reservó el catálogo.
        MaxStorageMB = externalRef?.ExternalMaxStorageMB ?? detail.MaxStorageMB,
        CurrentSizeMB = detail.CurrentSizeMB,
        LastActivityAt = detail.LastActivityAt,
        CreatedAt = detail.CreatedAt,
        PausedAt = detail.PausedAt,
        DeletedAt = detail.DeletedAt
    };

    /// <summary>
    /// Lee <c>Provisioning:{Engine}:MaxConcurrentConnections</c> (default,
    /// usado si el cliente no pidió nada) y
    /// <c>Provisioning:{Engine}:MaxConcurrentConnectionsCap</c> (tope duro) de
    /// configuración, y devuelve el valor final ya acotado. El cliente NUNCA
    /// puede superar el cap sin importar lo que pida — de lo contrario este
    /// control de abuso quedaría en manos del propio usuario que se quiere
    /// limitar.
    /// </summary>
    private int ResolveMaxConcurrentConnections(string engine, int? requested)
    {
        var defaultValue =
            int.TryParse(_config[$"Provisioning:{engine}:MaxConcurrentConnections"], out var d) ? d : 5;
        var cap =
            int.TryParse(_config[$"Provisioning:{engine}:MaxConcurrentConnectionsCap"], out var c) ? c : 20;

        var requestedOrDefault = requested ?? defaultValue;

        if (requestedOrDefault < 1) requestedOrDefault = 1;
        if (requestedOrDefault > cap) requestedOrDefault = cap;

        return requestedOrDefault;
    }
}
