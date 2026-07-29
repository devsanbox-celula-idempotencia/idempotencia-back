using idempotencia.Interfaces;
using idempotencia.Models;
using MySqlConnector;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de MySQL/MariaDB. Crea la base de datos, un usuario accesible
/// desde cualquier host ('%') y le concede privilegios ÚNICAMENTE sobre esa
/// base (GRANT ... ON db.*, nunca ON *.*). La cuota de tamaño no es nativa por
/// BD, por lo que <paramref name="maxStorageMb"/> no se aplica aquí (ver
/// docs/bugs.md — pendiente: job de monitoreo de tamaño real). El límite de
/// conexiones concurrentes sí es nativo (MAX_USER_CONNECTIONS) y se aplica.
/// </summary>
public class MySqlProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;

    public string Engine => DatabaseEngine.MySql;
    public string Host => _host;
    public int Port => _port;

    public MySqlProvisioner(IConfiguration config)
    {
        _adminConnectionString = config["Provisioning:MySql:AdminConnectionString"]
            ?? throw new InvalidOperationException("Falta Provisioning:MySql:AdminConnectionString.");
        _host = config["Provisioning:MySql:Host"] ?? "localhost";
        _port = int.TryParse(config["Provisioning:MySql:Port"], out var p) ? p : 3306;
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        var user = $"{QuoteLiteral(login)}@'%'";

        await ExecAsync(conn, $"CREATE DATABASE {QuoteIdentifier(dbName)}", ct);

        // MAX_USER_CONNECTIONS: tope de conexiones simultáneas de este login
        // sobre TODO el servidor (MySQL no tiene un límite "por BD", pero como
        // el usuario solo tiene permisos en su propia BD, en la práctica acota
        // el uso a esa BD). El valor ya viene resuelto (y acotado a un cap) por
        // DatabaseProvisioningService — evita que una cuenta agote el pool de
        // conexiones del servidor compartido.
        await ExecAsync(conn,
            $"CREATE USER {user} IDENTIFIED BY {QuoteLiteral(password)} " +
            $"WITH MAX_USER_CONNECTIONS {maxConcurrentConnections}", ct);

        await ExecAsync(conn, $"GRANT ALL PRIVILEGES ON {QuoteIdentifier(dbName)}.* TO {user}", ct);

        return new ProvisionResult(_host, _port);
    }

    public async Task DropAsync(string dbName, string login, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn, $"DROP DATABASE IF EXISTS {QuoteIdentifier(dbName)}", ct);
        await ExecAsync(conn, $"DROP USER IF EXISTS {QuoteLiteral(login)}@'%'", ct);
    }

    public async Task ChangePasswordAsync(string dbName, string login, string newPassword, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        var user = $"{QuoteLiteral(login)}@'%'";
        await ExecAsync(conn, $"ALTER USER {user} IDENTIFIED BY {QuoteLiteral(newPassword)}", ct);
    }

    public async Task DeactivateAsync(string dbName, string login, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        var user = $"{QuoteLiteral(login)}@'%'";
        // ACCOUNT LOCK impide iniciar sesión sin borrar el usuario ni sus
        // privilegios — reversible con ACCOUNT UNLOCK si se agrega "reactivar".
        await ExecAsync(conn, $"ALTER USER {user} ACCOUNT LOCK", ct);
    }

    public async Task ReactivateAsync(string dbName, string login, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        var user = $"{QuoteLiteral(login)}@'%'";
        // Inverso exacto del ACCOUNT LOCK de DeactivateAsync. Los privilegios
        // sobre la BD (GRANT ... ON db.*) nunca se revocaron, así que
        // desbloquear la cuenta deja al usuario como estaba.
        await ExecAsync(conn, $"ALTER USER {user} ACCOUNT UNLOCK", ct);
    }

    public async Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // information_schema.tables da datos + índices por tabla; la suma sobre
        // el schema es el tamaño de la BD. Es una ESTIMACIÓN del motor (InnoDB
        // no actualiza estas estadísticas en tiempo real), así que puede quedar
        // algo por debajo del tamaño real en disco justo después de una carga
        // grande. Es suficiente para mostrarle el uso al estudiante; si alguna
        // vez se usa para aplicar cuota (ítem 10), conviene revisar el margen.
        //
        // Si el schema no existe no hay filas y SUM devuelve NULL -> 0.
        const string sql = @"
            SELECT SUM(data_length + index_length) / 1024 / 1024
            FROM information_schema.tables
            WHERE table_schema = @DbName;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DbName", dbName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0m : Math.Round(Convert.ToDecimal(result), 2);
    }

    private static async Task ExecAsync(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";

    private static string QuoteLiteral(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
}
