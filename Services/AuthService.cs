using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Services;

/// <summary>
/// Implementación de <see cref="IAuthService"/>. Orquesta autenticación:
/// hashea/verifica contraseñas con BCrypt (SQL no valida el hash), invoca los
/// SPs vía repositorio y firma el JWT. No implementa reglas de negocio de
/// dominio: crear/vincular usuarios es responsabilidad de los SPs.
///
/// También dispara el aprovisionamiento automático de la BD MySQL del usuario
/// la primera vez que inicia sesión por contraseña (register o login) — ver
/// <see cref="EnsureMySqlDatabaseAsync"/>. NO se dispara en
/// <see cref="ExternalLoginAsync"/> (OAuth): esa respuesta viaja por redirect
/// con query string, no por JSON, y no hay forma segura de entregar ahí la
/// contraseña de la BD — ver el comentario en <see cref="ExternalLoginAsync"/>
/// y docs/bugs.md ítem 16.
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>Nombre lógico pedido al SP; este ya antepone el prefijo por usuario.</summary>
    private const string AutoProvisionedMySqlDbName = "principal";

    private readonly IUserRepository _users;
    private readonly IJwtTokenService _jwt;
    private readonly IDatabaseRepository _databases;
    private readonly IDatabaseProvisioningService _provisioning;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IJwtTokenService jwt,
        IDatabaseRepository databases,
        IDatabaseProvisioningService provisioning,
        ILogger<AuthService> logger)
    {
        _users = users;
        _jwt = jwt;
        _databases = databases;
        _provisioning = provisioning;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        // El hash se calcula en el backend; el SP solo persiste el valor recibido.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var identity = await _users.RegisterUserAsync(
            request.Email, passwordHash, request.FullName, ct);

        var response = _jwt.CreateToken(identity);
        response.MySqlDatabase = await EnsureMySqlDatabaseAsync(identity.UserId, ct);
        return response;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var login = await _users.GetLoginByEmailAsync(request.Email, ct);

        // Mensaje genérico e IDÉNTICO para los tres casos que no requieren
        // conocer la contraseña real (correo inexistente, cuenta solo-OAuth sin
        // PasswordHash, contraseña incorrecta). Antes, el caso "solo-OAuth" se
        // revisaba ANTES de verificar la contraseña y devolvía un mensaje
        // distinto ("Esta cuenta usa inicio de sesión externo.") sin que el
        // atacante necesitara acertar ninguna contraseña — eso permitía
        // enumerar qué correos existen y cuáles son cuentas OAuth-only con
        // solo mandar POST /auth/login con cualquier password. Ver
        // docs/bugs.md ítem 14.
        if (login is null || string.IsNullOrEmpty(login.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(request.Password, login.PasswordHash))
        {
            throw new AuthException("Credenciales inválidas.");
        }

        // Este chequeo SÍ puede ir después y con mensaje distinto: para verlo,
        // el atacante ya tuvo que acertar la contraseña real, así que el
        // "leak" de que la cuenta existe y está inactiva ya no es explotable
        // por fuerza bruta (si acertó la contraseña, ya sabía que la cuenta existe).
        if (!login.IsActive)
            throw new AuthException("La cuenta está inactiva.");

        var identity = new UserIdentity
        {
            UserId = login.UserId,
            Email = login.Email,
            FullName = login.FullName,
            Role = login.Role
        };

        var response = _jwt.CreateToken(identity);
        response.MySqlDatabase = await EnsureMySqlDatabaseAsync(identity.UserId, ct);
        return response;
    }

    public async Task<AuthResponse> ExternalLoginAsync(
        string provider, string providerUserId, string email, string fullName,
        CancellationToken ct = default)
    {
        // El SP resuelve/crea/vincula el usuario OAuth y devuelve su identidad.
        var identity = await _users.UpsertExternalLoginAsync(
            provider, providerUserId, email, fullName, ct);

        // A propósito NO se llama EnsureMySqlDatabaseAsync aquí (a diferencia
        // de Register/Login). Este AuthResponse nunca se serializa como JSON:
        // AuthController.ExternalCallback lo pasa a OAuthRedirectBuilder, que
        // arma una redirección con query string, y esa clase NO reenvía
        // MySqlDatabase (con razón — sería exponer la contraseña real de la BD
        // en la URL, historial del navegador y logs). Si auto-aprovisionáramos
        // aquí iguial, la contraseña generada quedaría guardada solo como HASH
        // en el catálogo y se perdería para siempre sin que el usuario la haya
        // visto nunca: una BD huérfana e inutilizable. Ver docs/bugs.md ítem 16.
        // El frontend debe llamar POST /databases (engine=MySql) explícitamente
        // apenas aterriza en /oauth/callback si es el primer login del usuario
        // — esa respuesta sí es JSON normal y sí entrega las credenciales.
        return _jwt.CreateToken(identity);
    }

    /// <summary>
    /// Si el usuario aún no tiene ninguna BD MySQL en el catálogo, la
    /// aprovisiona automáticamente (requisito: "al iniciar sesión por primera
    /// vez deberá crearse automáticamente una base de datos MySQL"). Idempotente
    /// entre logins: solo crea si <c>sp_GetUserDatabases</c> no devuelve ya una
    /// fila con Engine=MySql para este usuario.
    ///
    /// Deliberadamente NO propaga la excepción si el aprovisionamiento falla
    /// (motor caído, cuota agotada, etc.): la autenticación es una
    /// responsabilidad separada y no debe romperse por un problema de
    /// infraestructura de bases de datos. El error queda en logs para
    /// diagnóstico; el frontend recibirá <c>MySqlDatabase = null</c> y puede
    /// reintentar más tarde vía <c>POST /databases</c>.
    /// </summary>
    private async Task<ProvisionedDatabaseCredentials?> EnsureMySqlDatabaseAsync(
        int userId, CancellationToken ct)
    {
        try
        {
            var existing = await _databases.GetUserDatabasesAsync(userId, ct);
            var alreadyHasMySql = existing.Any(db =>
                string.Equals(db.Engine, DatabaseEngine.MySql, StringComparison.OrdinalIgnoreCase));

            if (alreadyHasMySql)
                return null;

            var result = await _provisioning.ProvisionAsync(
                userId, DatabaseEngine.MySql, AutoProvisionedMySqlDbName, ct: ct);

            return new ProvisionedDatabaseCredentials
            {
                DatabaseId = result.DatabaseId,
                Engine = result.Engine,
                DbName = result.DbName,
                Host = result.Host,
                Port = result.Port,
                LoginName = result.LoginName,
                Password = result.Password
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "No se pudo auto-aprovisionar la BD MySQL para el usuario {UserId}. " +
                "El login continúa igual; el usuario puede reintentar vía POST /databases.",
                userId);
            return null;
        }
    }
}
