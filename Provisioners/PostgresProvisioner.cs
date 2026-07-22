using idempotencia.Interfaces;
using idempotencia.Models;
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

    public string Engine => DatabaseEngine.Postgres;

    public PostgresProvisioner(IConfiguration config)
    {
        _adminConnectionString = config["Provisioning:Postgres:AdminConnectionString"]
            ?? throw new InvalidOperationException("Falta Provisioning:Postgres:AdminConnectionString.");
        _host = config["Provisioning:Postgres:Host"] ?? "localhost";
        _port = int.TryParse(config["Provisioning:Postgres:Port"], out var p) ? p : 5432;
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

    public async Task DropAsync(string dbName, string login, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn, $"DROP DATABASE IF EXISTS {QuoteIdentifier(dbName)}", ct);
        await ExecAsync(conn, $"DROP ROLE IF EXISTS {QuoteIdentifier(login)}", ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
