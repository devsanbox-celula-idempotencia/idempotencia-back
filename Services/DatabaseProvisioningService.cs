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

        // 3. Contraseña generada en el backend (solo se devuelve una vez).
        var password = PasswordGenerator.Generate();

        try
        {
            // 4. Creación física en el motor correspondiente.
            var result = await provisioner.CreateAsync(
                reservation.DbName, reservation.LoginName, password, reservation.MaxStorageMB,
                effectiveMaxConcurrentConnections, ct);

            // 5. Confirma en el catálogo y guarda el HASH de la contraseña.
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);
            await _repo.ConfirmDatabaseAsync(reservation.DatabaseId, passwordHash, ct);

            // Cadenas de conexión ya armadas para el usuario (con el parámetro de
            // TLS del motor incluido). Es solo construcción de strings sobre datos
            // que ya tenemos, así que va después de confirmar: si algo falló antes,
            // no hay conexión que entregar. Ver docs/bugs.md ítem 28.
            var clientConn = provisioner.BuildClientConnection(
                reservation.DbName, reservation.LoginName, password);

            return new CreateDatabaseResponse
            {
                DatabaseId = reservation.DatabaseId,
                Engine = engine,
                DbName = reservation.DbName,
                Status = "Active",
                MaxStorageMB = reservation.MaxStorageMB,
                MaxConcurrentConnections = effectiveMaxConcurrentConnections,
                Host = result.Host,
                Port = result.Port,
                LoginName = reservation.LoginName,
                Password = password,
                ConnectionUri = clientConn.Uri,
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
                await provisioner.DropAsync(reservation.DbName, reservation.LoginName, ct);
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
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

        var provisioner = _factory.Get(detail.Engine);
        return MapToDetailResponse(detail, provisioner);
    }

    public async Task<DatabaseDetailResponse> DeactivateAsync(
        int userId, int databaseId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

        if (!string.Equals(detail.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "Solo se puede desactivar una base de datos que esté activa.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);

        // Revoca el acceso físico PRIMERO; si esto falla, el catálogo sigue
        // reflejando la realidad (la BD sigue Active) en vez de quedar
        // desincronizado.
        await provisioner.DeactivateAsync(detail.DbName, detail.LoginName, ct);
        await _repo.DeactivateDatabaseAsync(databaseId, userId, ct);

        detail.Status = "Inactive";
        detail.PausedAt = DateTime.UtcNow;
        return MapToDetailResponse(detail, provisioner);
    }

    public async Task<DatabaseDetailResponse> ReactivateAsync(
        int userId, int databaseId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

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
        // los cuatro motores el reintento se completa limpio. Es el estado
        // desincronizado recuperable de los dos.
        await provisioner.ReactivateAsync(detail.DbName, detail.LoginName, ct);
        await _repo.ReactivateDatabaseAsync(databaseId, userId, ct);

        detail.Status = "Active";
        detail.PausedAt = null;
        return MapToDetailResponse(detail, provisioner);
    }

    public async Task DeleteAsync(int userId, int databaseId, CancellationToken ct = default)
    {
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

        if (!string.Equals(detail.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "La base de datos debe estar inactiva antes de poder eliminarla. " +
                "Desactívala primero con POST /databases/{id}/deactivate.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);

        // Borrado físico real (irreversible). Se marca en el catálogo recién
        // después de que el motor confirme el borrado.
        await provisioner.DropAsync(detail.DbName, detail.LoginName, ct);
        await _repo.MarkDatabaseDeletedAsync(databaseId, userId, ct);
    }

    public async Task ResetPasswordAsync(
        int userId, int databaseId, string userEmail, string userFullName, CancellationToken ct = default)
    {
        var detail = await _repo.GetDatabaseDetailAsync(databaseId, userId, ct)
            ?? throw new NotFoundException("Base de datos no encontrada.");

        if (!string.Equals(detail.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new AppException(
                "Solo se puede restablecer la contraseña de una base de datos activa.",
                StatusCodes.Status400BadRequest);

        var provisioner = _factory.Get(detail.Engine);
        var newPassword = PasswordGenerator.Generate();

        // Cambia la contraseña física PRIMERO; si falla, la contraseña
        // anterior sigue siendo la válida y no se toca el catálogo.
        await provisioner.ChangePasswordAsync(detail.DbName, detail.LoginName, newPassword, ct);

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _repo.ResetDatabasePasswordAsync(databaseId, userId, passwordHash, ct);

        // El correo es la ÚNICA forma en que el usuario recibe esta
        // contraseña — a propósito no se devuelve en la respuesta HTTP (no
        // queda en logs de acceso/historial del navegador). Si el envío
        // falla, SÍ se propaga la excepción (mapea a 500 genérico): la
        // contraseña física ya cambió, así que el usuario debe enterarse de
        // que algo salió mal para poder reintentar o contactar soporte, en
        // vez de quedarse bloqueado sin saberlo.
        var subject = $"Colmena — nueva contraseña para {detail.DbName}";
        var body = EmailTemplates.DatabasePasswordReset(
            detail, newPassword,
            provisioner.BuildClientConnection(detail.DbName, detail.LoginName, newPassword));
        await _email.SendAsync(userEmail, userFullName, subject, body, ct);
    }

    private static DatabaseDetailResponse MapToDetailResponse(
        ProvisionedDatabaseDetail detail, IDatabaseProvisioner provisioner) => new()
    {
        DatabaseId = detail.DatabaseId,
        Engine = detail.Engine,
        DbName = detail.DbName,
        Status = detail.Status,
        Host = provisioner.Host,
        Port = provisioner.Port,
        LoginName = detail.LoginName,
        MaxStorageMB = detail.MaxStorageMB,
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
