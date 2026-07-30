using System.Data;
using idempotencia.Interfaces;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

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
    public string Host => _host;
    public int Port => _port;

    public SqlServerProvisioner(
        IConfiguration config, IOptions<ProvisioningSettings> provisioning)
    {
        // Cadena de administración dedicada; si no hay, se reusa la del catálogo
        // (misma instancia, conectado como sa). Nunca hardcodear.
        _adminConnectionString =
            config["Provisioning:SqlServer:AdminConnectionString"]
            ?? config.GetConnectionString("Colmena")
            ?? throw new InvalidOperationException(
                "Falta la cadena de administración de SQL Server para aprovisionar.");

        // El host que se le entrega al usuario NO es el que usa el backend para
        // hablar con el motor (ese va en AdminConnectionString y en despliegue es
        // el nombre del contenedor): es la IP pública del VPS, común a los cuatro
        // motores. Program.cs ya validó al arrancar que esté configurada.
        _host = provisioning.Value.IpVps;
        _port = int.TryParse(config["Provisioning:SqlServer:Port"], out var p) ? p : 1433;
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        // maxConcurrentConnections: SQL Server no tiene un límite nativo por
        // login (se ignora aquí a propósito). Ver docs/bugs.md ítem 12 para la
        // propuesta con logon trigger, no aplicada todavía.
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

        // 2b. Sin esto, CUALQUIER login puede ver los nombres de TODAS las BDs
        // del servidor en sys.databases / Object Explorer de SSMS (comportamiento
        // por defecto de SQL Server), aunque no pueda entrar a ellas. En un
        // servidor multi-inquilino (una BD por estudiante) eso ya es una fuga de
        // información no aceptable. DENY VIEW ANY DATABASE oculta las BDs a las
        // que este login no tiene acceso explícito; sigue viendo master/tempdb y
        // la suya propia.
        await ExecAsync(conn, $"DENY VIEW ANY DATABASE TO {lg};", ct);

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

    public async Task ChangePasswordAsync(string dbName, string login, string newPassword, CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn, $"ALTER LOGIN {lg} WITH PASSWORD = {QuoteLiteral(newPassword)};", ct);
    }

    public async Task DeactivateAsync(string dbName, string login, CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // DISABLE impide iniciar sesión con ese login sin borrar el usuario ni
        // sus permisos dentro de la BD — reversible con ENABLE si en el futuro
        // se agrega un endpoint de reactivar.
        await ExecAsync(conn, $"ALTER LOGIN {lg} DISABLE;", ct);
    }

    public async Task ReactivateAsync(string dbName, string login, CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // Inverso exacto del DISABLE de DeactivateAsync. El login nunca se
        // borró ni perdió sus permisos dentro de la BD, así que ENABLE basta
        // para dejarlo como estaba, con la misma contraseña.
        await ExecAsync(conn, $"ALTER LOGIN {lg} ENABLE;", ct);
    }

    public async Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // sys.master_files reporta el espacio ASIGNADO a los archivos de la BD
        // (datos + log), en páginas de 8 KB. Se usa ese número y no el espacio
        // realmente ocupado dentro de los archivos porque es lo que se compara
        // contra MAXSIZE: SQL Server aplica la cuota sobre el tamaño del
        // archivo, así que es la métrica que le importa al estudiante para
        // saber cuánto le queda antes de que el motor le rechace escrituras.
        //
        // Se consulta con parámetro y DB_ID en vez de concatenar el nombre.
        // Si la BD no existe, DB_ID devuelve NULL, no hay filas, y SUM da NULL
        // -> se traduce a 0 más abajo.
        const string sql = @"
            SELECT SUM(CAST(mf.size AS BIGINT)) * 8.0 / 1024.0
            FROM sys.master_files mf
            WHERE mf.database_id = DB_ID(@DbName);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@DbName", SqlDbType.NVarChar, 128) { Value = dbName });

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0m : Math.Round(Convert.ToDecimal(result), 2);
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
