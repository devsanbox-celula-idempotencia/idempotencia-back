using System.Data;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de SQL Server. Crea físicamente la BD con tope de tamaño, el
/// login a nivel servidor y el usuario con permisos dentro de su propia BD. Los
/// nombres vienen ya validados por el catálogo; aun así se citan con
/// <see cref="QuoteIdentifier"/> como defensa en profundidad.
///
/// <b>Desde 2026-08-14 aprovisiona en un servidor DISTINTO al del catálogo.</b>
/// Las bases de los estudiantes viven en la instancia que provee Raft Consensus
/// (<c>Provisioning:SqlServer:AdminConnectionString</c>, base
/// <c>idempotencia_db</c>, con un login propio que tiene permiso de
/// <c>CREATE DATABASE</c> y de crear logins); el catálogo —tablas y SPs de
/// control— se queda donde estaba (<c>ConnectionStrings:Colmena</c>). Son dos
/// servidores que ya no comparten nada, y esa separación es justamente lo que
/// esta clase tiene que respetar: NUNCA usa la cadena del catálogo, ni siquiera
/// como respaldo (ver el constructor).
///
/// Dos consecuencias prácticas de que el servidor ya no sea nuestro:
///
/// <list type="bullet">
///   <item>
///     La cadena de administración ya no apunta a <c>master</c> sino a
///     <c>idempotencia_db</c>, y SQL Server solo acepta permisos de ÁMBITO
///     SERVIDOR cuando la base actual es <c>master</c> — de ahí el
///     <c>EXEC('USE master; ...')</c> del <c>DENY</c> en
///     <see cref="CreateAsync"/>.
///   </item>
///   <item>
///     El login del backend no es <c>sa</c>: tiene los permisos justos que
///     concedió el proveedor. Lo que pueda faltar se maneja explícitamente en
///     vez de tumbar el aprovisionamiento entero (ver el <c>DENY</c> y
///     <see cref="GetSizeMbAsync"/>).
///   </item>
/// </list>
/// </summary>
public class SqlServerProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _requireTls;
    private readonly bool _denyViewAnyDatabase;
    private readonly ILogger<SqlServerProvisioner> _logger;

    public string Engine => DatabaseEngine.SqlServer;
    public string Host => _host;
    public int Port => _port;

    public SqlServerProvisioner(
        IConfiguration config, IOptions<ProvisioningSettings> provisioning,
        ILogger<SqlServerProvisioner> logger)
    {
        _logger = logger;

        // Cadena de administración DEDICADA y obligatoria. Antes, si faltaba, se
        // caía en la del catálogo — cuando ambos vivían en la misma instancia eso
        // era inofensivo. Ya no: con el aprovisionamiento movido al servidor de
        // Raft, ese respaldo silencioso crearía las bases de los estudiantes
        // DENTRO del servidor del catálogo, mezclando justo lo que se separó, y
        // sin que nada fallara hasta que alguien lo notara a mano. Se prefiere no
        // arrancar. Es el mismo criterio del localhost silencioso que documenta
        // ProvisioningSettings.
        _adminConnectionString =
            config["Provisioning:SqlServer:AdminConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta Provisioning:SqlServer:AdminConnectionString: la cadena del servidor " +
                "donde se crean las bases de los estudiantes. NO se reusa la del catálogo " +
                "(ConnectionStrings:Colmena) a propósito — son dos servidores distintos.");

        // El host que se le entrega al usuario NO es el que usa el backend para
        // hablar con el motor (ese va en AdminConnectionString). Para los motores
        // que corren en el VPS propio es Provisioning:IpVps, común a todos; SQL
        // Server ya no es uno de ellos —vive en la infraestructura de Raft— así
        // que admite un host propio y solo cae a IpVps si no está configurado,
        // para no romper un ambiente local donde todo siga junto.
        _host = config["Provisioning:SqlServer:PublicHost"] is { Length: > 0 } publicHost
            ? publicHost
            : provisioning.Value.IpVps;
        _port = int.TryParse(config["Provisioning:SqlServer:Port"], out var p) ? p : 1433;

        // El DENY de abajo necesita permisos de ámbito servidor que el proveedor
        // pudo no habernos concedido. La clave permite apagarlo sin recompilar
        // si resulta que no los tenemos; ver la nota en CreateAsync.
        _denyViewAnyDatabase =
            !bool.TryParse(config["Provisioning:SqlServer:DenyViewAnyDatabase"], out var deny) || deny;

        // Ver la nota de RequireTls en MySqlProvisioner. En SQL Server arranca
        // prendido: el motor soporta cifrado siempre (genera un certificado
        // autofirmado al instalarse) y la propia cadena de administración del
        // backend ya se conecta con Encrypt=True.
        _requireTls = bool.TryParse(config["Provisioning:SqlServer:RequireTls"], out var tls) && tls;
    }

    /// <inheritdoc />
    public ClientConnectionInfo BuildClientConnection(string dbName, string login, string password)
    {
        // SQL Server no usa URIs: su formato nativo es la cadena de keywords de
        // ADO.NET, que es la que aceptan SSMS y Azure Data Studio (y la misma
        // que usa el backend). TrustServerCertificate=True acompaña a
        // Encrypt=True porque el certificado es autofirmado.
        //
        // La contraseña se interpola sin comillas a propósito: PasswordGenerator
        // no emite ';' '"' ''' ni espacios, los únicos caracteres que obligarían
        // a citar el valor en este formato.
        var tls = _requireTls ? "Encrypt=True;TrustServerCertificate=True;" : string.Empty;
        var jdbcTls = _requireTls ? ";encrypt=true;trustServerCertificate=true" : string.Empty;

        return new ClientConnectionInfo(
            $"Server={_host},{_port};Database={dbName};User Id={login};Password={password};{tls}",
            $"jdbc:sqlserver://{_host}:{_port};databaseName={dbName}{jdbcTls}");
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

        // Los identificadores que viajan DENTRO del literal de un EXEC('...')
        // atraviesan dos capas de parseo, así que necesitan una vuelta más de
        // escape: QuoteIdentifier protege el corchete, pero una comilla simple
        // cerraría el literal exterior. Hoy el catálogo y el regex del DTO no
        // dejan pasar comillas, así que es defensa en profundidad — la misma
        // razón por la que se citan los identificadores de entrada.
        var dbInExec = EscapeForDynamicSql(db);
        var lgInExec = EscapeForDynamicSql(lg);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // 1. Crear la BD, y recién después aplicarle la cuota.
        //
        // Va en DOS statements y no en uno solo con la cláusula ON PRIMARY (...)
        // porque esa forma exige FILENAME: en cuanto se escribe un <filespec>,
        // SQL Server pide la ruta física del archivo y falla con el error 1036
        // ("File option FILENAME is required in this CREATE/ALTER DATABASE
        // statement"). Y esa ruta no la podemos escribir: el motor vive en la
        // infraestructura del proveedor y su directorio de datos no es asunto
        // nuestro. Un CREATE DATABASE pelado deja que el servidor elija dónde
        // poner los archivos, que es justo lo que queremos.
        try
        {
            await ExecAsync(conn, $"CREATE DATABASE {db};", ct);
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // 1801 = "Database already exists". En este servidor no es un caso
            // raro: el proveedor BLOQUEA el DROP por SQL (ver DropAsync), así
            // que una creación que falló a mitad deja la BD viva y el catálogo
            // sin registro. El siguiente intento con el mismo nombre choca acá.
            // El mensaje genérico ("ya existe") no le sirve a nadie: hay que
            // decir qué hacer.
            throw new AppException(
                $"Ya existe una base de datos llamada '{dbName}' en el servidor. " +
                "Suele ser el residuo de un intento anterior que falló: el proveedor no " +
                "permite borrar bases por SQL, así que hay que eliminarla desde el panel " +
                "de Raft antes de reintentar, o crear la base con otro nombre.",
                StatusCodes.Status409Conflict);
        }

        // 1b. La cuota real. MAXSIZE es lo que de verdad frena al estudiante:
        // SQL Server rechaza las escrituras cuando el archivo llega a ese tope,
        // en vez de dejarlo crecer y comerse el disco compartido.
        //
        // NAME es el nombre LÓGICO del archivo de datos, que tras un
        // CREATE DATABASE sin filespec es siempre el nombre de la base (el log
        // queda como '<base>_log', y no se toca: crece poco y limitarlo puede
        // dejar la BD en solo lectura por una transacción larga).
        //
        // No se fija SIZE a propósito: MODIFY FILE no puede REDUCIR el tamaño
        // actual, así que pedir un SIZE igual o menor al que heredó de 'model'
        // haría fallar la creación entera por un dato que no aporta nada.
        //
        // FILEGROWTH se acota a la cuota: SQL Server rechaza con el error 5169
        // ("FILEGROWTH cannot be greater than MAXSIZE") cualquier incremento
        // mayor que el tope, y tiene razón — un archivo que crece de a 4 MB no
        // cabe en una cuota de 2 MB. El valor sale del catálogo, así que no se
        // puede asumir que siempre sea holgado.
        if (maxStorageMb > 0)
        {
            var growthMb = Math.Max(1, Math.Min(4, maxStorageMb));

            await ExecAsync(conn,
                $"ALTER DATABASE {db} MODIFY FILE " +
                $"(NAME = {QuoteLiteral(dbName)}, MAXSIZE = {maxStorageMb}MB, FILEGROWTH = {growthMb}MB);",
                ct);
        }
        else
        {
            // Cuota no configurada en el catálogo: se deja el crecimiento por
            // defecto en vez de mandar MAXSIZE = 0MB, que SQL Server rechaza.
            // La BD queda usable y sin tope — se registra para que se note.
            _logger.LogWarning(
                "La BD {Db} se creó SIN cuota de almacenamiento: el catálogo reportó " +
                "MaxStorageMB = {MaxStorageMb}. Revisar sp_ReserveDatabase.", dbName, maxStorageMb);
        }

        // 2. Login a nivel servidor. QuoteLiteral escapa la contraseña como literal.
        await ExecAsync(conn,
            $"CREATE LOGIN {lg} WITH PASSWORD = {QuoteLiteral(password)}, CHECK_POLICY = OFF;", ct);

        // 2b. Sin esto, CUALQUIER login puede ver los nombres de TODAS las BDs
        // del servidor en sys.databases / Object Explorer de SSMS (comportamiento
        // por defecto de SQL Server), aunque no pueda entrar a ellas. En un
        // servidor multi-inquilino (una BD por estudiante, y encima compartido
        // con otros equipos) eso ya es una fuga de información no aceptable.
        // DENY VIEW ANY DATABASE oculta las BDs a las que este login no tiene
        // acceso explícito; sigue viendo master/tempdb y la suya propia.
        //
        // Va dentro de EXEC('USE master; ...') y no suelto: es un permiso de
        // ÁMBITO SERVIDOR, y SQL Server los acepta únicamente cuando la base
        // actual es master. Mientras la cadena de administración apuntaba a
        // master el statement suelto funcionaba; desde que apunta a
        // idempotencia_db (servidor de Raft) fallaría. El SP de ejemplo que
        // entregó el proveedor hace exactamente lo mismo por esta razón.
        if (_denyViewAnyDatabase)
        {
            try
            {
                await ExecAsync(conn, $"EXEC('USE master; DENY VIEW ANY DATABASE TO {lgInExec};');", ct);
            }
            catch (SqlException ex)
            {
                // Este DENY exige permisos de ámbito servidor que el proveedor
                // pudo no habernos concedido. Se registra como ERROR —es una
                // protección real que se está perdiendo— pero NO se aborta: la
                // alternativa es que nadie pueda crear una base de SQL Server
                // hasta que alguien negocie el permiso, y eso es peor que un
                // estudiante pudiendo LISTAR nombres de bases a las que no puede
                // entrar. Si se decide que no se puede vivir sin esto, se apaga
                // Provisioning:SqlServer:DenyViewAnyDatabase y se pide el
                // permiso a Raft (soporte: consensusraft@gmail.com).
                _logger.LogError(ex,
                    "No se pudo aplicar DENY VIEW ANY DATABASE al login {Login}: el login de " +
                    "administración no tiene permisos de ámbito servidor suficientes. La BD queda " +
                    "creada y utilizable, pero ese login podrá ver los NOMBRES de las demás bases " +
                    "del servidor. Solicitar el permiso al proveedor.", login);
            }
        }

        // 3. Usuario dentro de la BD + db_owner sobre SU BD. USE dentro de EXEC
        //    porque no se puede cambiar de BD en una conexión ya abierta.
        await ExecAsync(conn,
            $"EXEC('USE {dbInExec}; CREATE USER {lgInExec} FOR LOGIN {lgInExec}; " +
            $"ALTER ROLE db_owner ADD MEMBER {lgInExec};');",
            ct);

        return new ProvisionResult(_host, _port);
    }

    public async Task DropAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var db = QuoteIdentifier(dbName);
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // 1. Revocar el acceso PRIMERO. Es lo único que este servidor nos
        // garantiza poder hacer, y es lo que de verdad protege los datos: sin
        // login no hay forma de conectarse a la base, exista o no. El orden
        // importa — si se dejara para el final, un DROP DATABASE bloqueado se
        // llevaría por delante también la revocación.
        await ExecAsync(conn,
            $"IF SUSER_ID({QuoteLiteral(login)}) IS NOT NULL DROP LOGIN {lg};", ct);

        // 2. Borrado físico, que en el servidor del proveedor PUEDE ESTAR
        // PROHIBIDO. Raft tiene un trigger DDL que cancela cualquier
        // DROP DATABASE por SQL y responde "utiliza el panel de Raft".
        //
        // No se propaga la excepción a propósito: el acceso ya quedó revocado y
        // el catálogo puede marcar la base como eliminada sin mentir sobre lo
        // que importa. Propagar dejaría al usuario sin poder cerrar el ciclo de
        // vida de su base por una restricción que no es suya ni nuestra. Lo que
        // sí queda es un ERROR en el log con el nombre exacto a purgar.
        try
        {
            await ExecAsync(conn,
                $"IF DB_ID({QuoteLiteral(dbName)}) IS NOT NULL BEGIN " +
                $"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {db}; END", ct);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex,
                "No se pudo borrar físicamente la BD {Db}: el servidor rechazó el DROP DATABASE. " +
                "El login ya fue revocado, así que nadie puede conectarse, pero la base sigue " +
                "ocupando espacio. Eliminarla desde el panel del proveedor.", dbName);

            // El SET SINGLE_USER de arriba se ejecuta en su propia transacción
            // implícita, así que el rollback del trigger NO lo deshace: sin esto
            // la base quedaría aceptando una sola conexión, que es peor que el
            // estado en el que estaba. Se devuelve a MULTI_USER.
            try
            {
                await ExecAsync(conn,
                    $"IF DB_ID({QuoteLiteral(dbName)}) IS NOT NULL " +
                    $"ALTER DATABASE {db} SET MULTI_USER;", ct);
            }
            catch (SqlException restoreEx)
            {
                _logger.LogError(restoreEx,
                    "Además, la BD {Db} pudo quedar en SINGLE_USER tras el intento de borrado. " +
                    "Revisar y ejecutar ALTER DATABASE ... SET MULTI_USER a mano.", dbName);
            }
        }
    }

    public async Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await ExecAsync(conn, $"ALTER LOGIN {lg} WITH PASSWORD = {QuoteLiteral(newPassword)};", ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
    }

    public async Task DeactivateAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // DISABLE impide iniciar sesión con ese login sin borrar el usuario ni
        // sus permisos dentro de la BD — reversible con ENABLE si en el futuro
        // se agrega un endpoint de reactivar.
        await ExecAsync(conn, $"ALTER LOGIN {lg} DISABLE;", ct);
    }

    public async Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var lg = QuoteIdentifier(login);

        await using var conn = new SqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // Inverso exacto del DISABLE de DeactivateAsync. El login nunca se
        // borró ni perdió sus permisos dentro de la BD, así que ENABLE basta
        // para dejarlo como estaba, con la misma contraseña.
        await ExecAsync(conn, $"ALTER LOGIN {lg} ENABLE;", ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
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
        //
        // Se devuelven DOS cosas porque "no hay filas" es ambiguo desde que el
        // servidor no es nuestro y el login no es sa: puede significar que la BD
        // ya no existe (0, dato válido) o que existe pero sys.master_files no nos
        // deja verla por falta de permisos (-1, "no medible"). Distinguirlas
        // importa: si se colapsaran en 0, un permiso corto haría que el job
        // pisara el tamaño real de TODAS las bases con ceros, y la API reportaría
        // 0.00 MB para bases llenas. Ver la convención en
        // IDatabaseProvisioner.GetSizeMbAsync.
        const string sql = @"
            SELECT CAST(CASE WHEN DB_ID(@DbName) IS NULL THEN 1 ELSE 0 END AS BIT) AS NoExiste,
                   (SELECT SUM(CAST(mf.size AS BIGINT)) * 8.0 / 1024.0
                    FROM sys.master_files mf
                    WHERE mf.database_id = DB_ID(@DbName)) AS TamanoMb;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@DbName", SqlDbType.NVarChar, 128) { Value = dbName });

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return -1m;

        // La BD desapareció del motor: para el job eso no es un error, es un 0
        // legítimo (y el catálogo se entera de que quedó desincronizado).
        if (reader.GetBoolean(0))
            return 0m;

        // Existe pero no devolvió tamaño -> no la podemos ver. Se conserva el
        // último valor conocido en vez de inventar un cero.
        if (await reader.IsDBNullAsync(1, ct))
            return -1m;

        return Math.Round(reader.GetDecimal(1), 2);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Duplica las comillas simples de un fragmento que se va a incrustar dentro
    // del literal de un EXEC('...'): ese literal es una capa de parseo extra que
    // QuoteIdentifier no cubre.
    private static string EscapeForDynamicSql(string quoted) => quoted.Replace("'", "''");

    // [identificador] con escape de ] duplicado (equivalente a QUOTENAME).
    private static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

    // 'literal' con escape de comilla simple.
    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
