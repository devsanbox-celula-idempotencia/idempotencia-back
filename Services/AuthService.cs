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
/// la primera vez que inicia sesión — ver <see cref="EnsureMySqlDatabaseAsync"/>.
/// En register/login por contraseña las credenciales vuelven en el AuthResponse
/// (JSON). En OAuth (<see cref="ExternalLoginAsync"/>) la respuesta viaja por
/// redirect/query string, donde no se puede entregar un secreto de forma
/// segura, así que las credenciales se envían por CORREO — ver docs/bugs.md
/// ítem 16.
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>Nombre lógico pedido al SP; este ya antepone el prefijo por usuario.</summary>
    private const string AutoProvisionedMySqlDbName = "principal";

    private readonly IUserRepository _users;
    private readonly IJwtTokenService _jwt;
    private readonly IDatabaseRepository _databases;
    private readonly IDatabaseProvisioningService _provisioning;
    private readonly IEmailService _email;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IJwtTokenService jwt,
        IDatabaseRepository databases,
        IDatabaseProvisioningService provisioning,
        IEmailService email,
        ILogger<AuthService> logger)
    {
        _users = users;
        _jwt = jwt;
        _databases = databases;
        _provisioning = provisioning;
        _email = email;
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

        // Igual que register/login, en el primer inicio de sesión se aprovisiona
        // la BD MySQL. PERO este AuthResponse nunca se serializa como JSON:
        // AuthController.ExternalCallback lo pasa a OAuthRedirectBuilder, que
        // arma una redirección con query string y NO reenvía MySqlDatabase (con
        // razón — sería exponer la contraseña real de la BD en la URL, historial
        // del navegador y logs). Por eso, cuando se crea la BD en este flujo, las
        // credenciales se entregan por CORREO (el único canal seguro para un
        // secreto de un solo uso acá) y NO se poblan en la respuesta. Así se
        // evita tanto la BD huérfana como la exposición en la URL. Ver bugs.md
        // ítem 16.
        var credentials = await EnsureMySqlDatabaseAsync(identity.UserId, ct);
        if (credentials is not null)
            await SendFirstDatabaseEmailAsync(identity.UserId, email, fullName, credentials, ct);

        return _jwt.CreateToken(identity);
    }

    /// <summary>
    /// Envía por correo las credenciales de la BD MySQL recién auto-aprovisionada
    /// en un login por OAuth. Es el canal seguro que reemplaza al AuthResponse
    /// (que en OAuth se pierde en el redirect). NO bloquea ni revierte el login
    /// si el correo falla: la BD ya existe y el usuario puede regenerar la
    /// contraseña con <c>POST /databases/{id}/reset-password</c> (que también la
    /// manda por correo). El error queda en logs.
    /// </summary>
    private async Task SendFirstDatabaseEmailAsync(
        int userId, string email, string fullName,
        ProvisionedDatabaseCredentials credentials, CancellationToken ct)
    {
        try
        {
            await _email.SendAsync(
                email,
                string.IsNullOrWhiteSpace(fullName) ? email : fullName,
                "Tu primera base de datos en Colmena",
                EmailTemplates.FirstDatabaseCredentials(credentials),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Se aprovisionó la BD MySQL {DatabaseId} para el usuario {UserId} en " +
                "login OAuth, pero falló el envío del correo con las credenciales. La " +
                "BD existe; el usuario puede regenerar la contraseña con " +
                "POST /databases/{{id}}/reset-password.",
                credentials.DatabaseId, userId);
        }
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
