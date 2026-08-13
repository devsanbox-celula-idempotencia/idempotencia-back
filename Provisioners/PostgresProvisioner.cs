using idempotencia.Interfaces;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Extensions.Options;
using Npgsql;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de PostgreSQL. Crea un rol con login y una base de datos de la
/// que ese rol es dueño. La cuota de tamaño no es nativa por BD en Postgres, por
/// lo que <paramref name="maxStorageMb"/> no se aplica aquí (ver docs/bugs.md).
/// El límite de conexiones concurrentes sí es nativo (CONNECTION LIMIT) y se
/// aplica al rol.
/// </summary>
public class PostgresProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _requireTls;

    public string Engine => DatabaseEngine.Postgres;
    public string Host => _host;
    public int Port => _port;

    public PostgresProvisioner(
        IConfiguration config, IOptions<ProvisioningSettings> provisioning)
    {
        _adminConnectionString = config["Provisioning:Postgres:AdminConnectionString"]
            ?? throw new InvalidOperationException("Falta Provisioning:Postgres:AdminConnectionString.");
        // El host que se le entrega al usuario NO es el que usa el backend para
        // hablar con el motor (ese va en AdminConnectionString y en despliegue es
        // el nombre del contenedor): es la IP pública del VPS, común a los cuatro
        // motores. Program.cs ya validó al arrancar que esté configurada.
        _host = provisioning.Value.IpVps;
        _port = int.TryParse(config["Provisioning:Postgres:Port"], out var p) ? p : 5432;

        // Ver la nota de RequireTls en MySqlProvisioner. En Postgres arranca
        // apagado: la imagen oficial no habilita TLS por defecto, y pedir
        // sslmode=require contra un servidor sin certificado hace fallar la
        // conexión del usuario. Prenderlo cuando el motor tenga TLS configurado.
        _requireTls = bool.TryParse(config["Provisioning:Postgres:RequireTls"], out var tls) && tls;
    }

    /// <inheritdoc />
    public ClientConnectionInfo BuildClientConnection(string dbName, string login, string password)
    {
        // sslmode=require: cifra sin validar la cadena de confianza del
        // certificado (verify-ca/verify-full sí la validan y fallarían con un
        // cert autofirmado). Misma grafía en libpq y en el driver JDBC.
        //
        // Postgres no tiene el problema de caching_sha2_password de MySQL —
        // SCRAM no necesita intercambio de clave RSA— así que acá TLS es solo
        // por confidencialidad de las credenciales en tránsito.
        var tls = _requireTls ? "?sslmode=require" : string.Empty;

        return new ClientConnectionInfo(
            $"postgresql://{Uri.EscapeDataString(login)}:{Uri.EscapeDataString(password)}" +
            $"@{_host}:{_port}/{dbName}{tls}",
            $"jdbc:postgresql://{_host}:{_port}/{dbName}{tls}");
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // CONNECTION LIMIT: tope nativo de Postgres para conexiones simultáneas
        // de este rol. El valor ya viene resuelto (y acotado a un cap) por
        // DatabaseProvisioningService — evita que una cuenta agote el pool del
        // servidor.
        await ExecAsync(conn,
            $"CREATE ROLE {QuoteIdentifier(login)} LOGIN PASSWORD {QuoteLiteral(password)} " +
            $"CONNECTION LIMIT {maxConcurrentConnections}", ct);
        await ExecAsync(conn, $"CREATE DATABASE {QuoteIdentifier(dbName)} OWNER {QuoteIdentifier(login)}", ct);

        // Por defecto Postgres concede CONNECT sobre toda BD nueva al rol
        // PUBLIC, es decir CUALQUIER otro usuario/rol del servidor (otros
        // estudiantes incluidos) puede conectarse a esta BD apenas se crea. En
        // un servidor multi-inquilino eso es exactamente lo que no se puede
        // permitir. Se revoca de PUBLIC y se concede explícitamente solo al
        // dueño.
        await ExecAsync(conn,
            $"REVOKE CONNECT ON DATABASE {QuoteIdentifier(dbName)} FROM PUBLIC", ct);
        await ExecAsync(conn,
            $"GRANT CONNECT ON DATABASE {QuoteIdentifier(dbName)} TO {QuoteIdentifier(login)}", ct);

        return new ProvisionResult(_host, _port);
    }

    public async Task DropAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn, $"DROP DATABASE IF EXISTS {QuoteIdentifier(dbName)}", ct);
        await ExecAsync(conn, $"DROP ROLE IF EXISTS {QuoteIdentifier(login)}", ct);
    }

    public async Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn,
            $"ALTER ROLE {QuoteIdentifier(login)} WITH PASSWORD {QuoteLiteral(newPassword)}", ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
    }

    public async Task DeactivateAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // NOLOGIN impide iniciar sesión sin borrar el rol ni sus privilegios —
        // reversible con LOGIN si se agrega "reactivar".
        await ExecAsync(conn, $"ALTER ROLE {QuoteIdentifier(login)} NOLOGIN", ct);
    }

    public async Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // Inverso exacto del NOLOGIN de DeactivateAsync. El rol conserva la
        // propiedad de la BD y todos sus privilegios; solo se le devuelve la
        // capacidad de iniciar sesión.
        await ExecAsync(conn, $"ALTER ROLE {QuoteIdentifier(login)} LOGIN", ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
    }

    public async Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // pg_database_size da el tamaño real en disco de la BD completa
        // (incluye índices y catálogos internos). Es el número más fiel de los
        // cuatro motores: no es una estimación, lo calcula el motor sobre los
        // archivos.
        //
        // Se pasa el nombre como parámetro (texto), no concatenado. Si la BD no
        // existe, pg_database_size lanza; se atrapa y se devuelve 0 para que el
        // job no se caiga por una base que ya no está.
        const string sql = "SELECT pg_database_size(@DbName) / 1024.0 / 1024.0;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DbName", dbName);

        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? 0m : Math.Round(Convert.ToDecimal(result), 2);
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000") // invalid_catalog_name
        {
            return 0m;
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
