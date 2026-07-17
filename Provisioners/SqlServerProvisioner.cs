using System.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using Microsoft.Data.SqlClient;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de SQL Server (ejemplo implementado). Crea físicamente la BD con
/// tope de tamaño, el login a nivel servidor y el usuario con permisos dentro de
/// su propia BD. Los nombres vienen ya validados por el catálogo; aun así se
/// citan con <see cref="QuoteIdentifier"/> como defensa en profundidad.
/// </summary>
public class SqlServerProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;

    public string Engine => DatabaseEngine.SqlServer;

    public SqlServerProvisioner(IConfiguration config)
    {
        // Cadena de administración dedicada; si no hay, se reusa la del catálogo
        // (misma instancia, conectado como sa). Nunca hardcodear.
        _adminConnectionString =
            config["Provisioning:SqlServer:AdminConnectionString"]
            ?? config.GetConnectionString("Colmena")
            ?? throw new InvalidOperationException(
                "Falta la cadena de administración de SQL Server para aprovisionar.");

        _host = config["Provisioning:SqlServer:Host"] ?? "localhost";
        _port = int.TryParse(config["Provisioning:SqlServer:Port"], out var p) ? p : 1433;
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb, CancellationToken ct = default)
    {
        var db = QuoteIdentifier(dbName);
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // 1. Crear la BD con MAXSIZE = cuota real (SQL Server impide crecer más).
        await ExecAsync(conn,
            $"CREATE DATABASE {db} ON PRIMARY " +
            $"(NAME = {QuoteLiteral(dbName + "_data")}, SIZE = 8MB, MAXSIZE = {maxStorageMb}MB, FILEGROWTH = 4MB);",
            ct);

        // 2. Login a nivel servidor. QuoteLiteral escapa la contraseña como literal.
        await ExecAsync(conn,
            $"CREATE LOGIN {lg} WITH PASSWORD = {QuoteLiteral(password)}, CHECK_POLICY = OFF;", ct);

        // 3. Usuario dentro de la BD + db_owner sobre SU BD. USE dentro de EXEC
        //    porque no se puede cambiar de BD en una conexión ya abierta.
        await ExecAsync(conn,
            $"EXEC('USE {db}; CREATE USER {lg} FOR LOGIN {lg}; ALTER ROLE db_owner ADD MEMBER {lg};');",
            ct);

        return new ProvisionResult(_host, _port);
    }

    public async Task DropAsync(string dbName, string login, CancellationToken ct = default)
    {
        var db = QuoteIdentifier(dbName);
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // Cierra conexiones activas y borra la BD si existe.
        await ExecAsync(conn,
            $"IF DB_ID({QuoteLiteral(dbName)}) IS NOT NULL BEGIN " +
            $"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {db}; END", ct);

        // Borra el login si existe.
        await ExecAsync(conn,
            $"IF SUSER_ID({QuoteLiteral(login)}) IS NOT NULL DROP LOGIN {lg};", ct);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // [identificador] con escape de ] duplicado (equivalente a QUOTENAME).
    private static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

    // 'literal' con escape de comilla simple.
    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
