using idempotencia.Interfaces;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de MongoDB contra un servidor Mongo PROPIO, hablado con el driver
/// nativo: materializa la base creando una colección inicial y registra un
/// usuario con rol readWrite sobre ella. La cuota de tamaño no es nativa por BD,
/// por lo que <paramref name="maxStorageMb"/> no se aplica aquí.
///
/// <b>Desde 2026-08-12 no es la implementación activa por defecto.</b> El motor
/// "Mongo" pasó a aprovisionarse contra la API externa del equipo
/// (<see cref="RemoteMongoProvisioner"/>), que es la que se registra en DI
/// cuando <c>Provisioning:Mongo:Remote:Enabled</c> está en <c>true</c>. Esta
/// clase se conserva —y se mantiene compilando— para poder volver atrás
/// poniendo esa clave en <c>false</c>, sin más cambios que reiniciar: es la
/// única forma de seguir operando las bases de Mongo creadas ANTES de la
/// migración, que viven en el servidor propio y no existen en la API externa.
/// Si algún día ya no queda ninguna, esta clase puede borrarse junto con
/// <c>Provisioning:Mongo:AdminConnectionString</c>.
/// </summary>
public class MongoProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _requireTls;

    public string Engine => DatabaseEngine.Mongo;
    public string Host => _host;
    public int Port => _port;

    public MongoProvisioner(
        IConfiguration config, IOptions<ProvisioningSettings> provisioning)
    {
        _adminConnectionString = config["Provisioning:Mongo:AdminConnectionString"]
            ?? throw new InvalidOperationException("Falta Provisioning:Mongo:AdminConnectionString.");
        // El host que se le entrega al usuario NO es el que usa el backend para
        // hablar con el motor (ese va en AdminConnectionString y en despliegue es
        // el nombre del contenedor): es la IP pública del VPS, común a los cuatro
        // motores. Program.cs ya validó al arrancar que esté configurada.
        _host = provisioning.Value.IpVps;
        _port = int.TryParse(config["Provisioning:Mongo:Port"], out var p) ? p : 27017;

        // Ver la nota de RequireTls en MySqlProvisioner. En Mongo arranca
        // apagado por la misma razón que en Postgres: la imagen oficial no trae
        // TLS habilitado y tls=true contra un servidor sin certificado corta la
        // conexión del usuario.
        _requireTls = bool.TryParse(config["Provisioning:Mongo:RequireTls"], out var tls) && tls;
    }

    /// <inheritdoc />
    public ClientConnectionInfo BuildClientConnection(string dbName, string login, string password)
    {
        // authSource es obligatorio, no un extra: el usuario se crea DENTRO de
        // su propia BD (no en 'admin'), así que sin este parámetro el cliente
        // intenta autenticarse contra 'admin' y falla con "Authentication
        // failed" — un error que parece de credenciales y no lo es.
        //
        // tlsInsecure acompaña a tls=true porque el certificado es autofirmado:
        // sin él, el driver corta por validación de la cadena de confianza.
        var tls = _requireTls ? "&tls=true&tlsInsecure=true" : string.Empty;

        // Mongo no tiene un driver JDBC estándar (los clientes usan el driver
        // nativo o mongosh), así que solo se entrega la URI.
        return new ClientConnectionInfo(
            $"mongodb://{Uri.EscapeDataString(login)}:{Uri.EscapeDataString(password)}" +
            $"@{_host}:{_port}/{dbName}?authSource={Uri.EscapeDataString(dbName)}{tls}",
            null);
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        // maxConcurrentConnections: Mongo no expone un límite nativo por
        // usuario (solo net.maxIncomingConnections a nivel de servidor
        // completo) — se ignora aquí a propósito. Ver docs/bugs.md ítem 12.
        var db = new MongoClient(_adminConnectionString).GetDatabase(dbName);

        await db.CreateCollectionAsync("_init", cancellationToken: ct);

        var createUser = new BsonDocument
        {
            { "createUser", login },
            { "pwd", password },
            { "roles", new BsonArray { new BsonDocument { { "role", "readWrite" }, { "db", dbName } } } }
        };
        await db.RunCommandAsync<BsonDocument>(createUser, cancellationToken: ct);

        return new ProvisionResult(_host, _port);
    }

    public async Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default)
    {
        // updateUser es a nivel de la BD donde vive el usuario (no "admin"),
        // igual que en CreateAsync/DropAsync — Mongo scopea el usuario a una
        // BD concreta, no es un identificador a nivel de servidor.
        var db = new MongoClient(_adminConnectionString).GetDatabase(dbName);

        var updateUser = new BsonDocument
        {
            { "updateUser", login },
            { "pwd", newPassword }
        };
        await db.RunCommandAsync<BsonDocument>(updateUser, cancellationToken: ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
    }

    public async Task DeactivateAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var db = new MongoClient(_adminConnectionString).GetDatabase(dbName);

        // Vaciar roles impide cualquier operación de lectura/escritura sin
        // borrar el usuario — reversible si se agrega "reactivar" (habría que
        // recordar el rol/BD original para volver a otorgarlo).
        var updateUser = new BsonDocument
        {
            { "updateUser", login },
            { "roles", new BsonArray() }
        };
        await db.RunCommandAsync<BsonDocument>(updateUser, cancellationToken: ct);
    }

    public async Task DropAsync(string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var client = new MongoClient(_adminConnectionString);
        var db = client.GetDatabase(dbName);

        try
        {
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "dropUser", login } }, cancellationToken: ct);
        }
        catch (MongoException)
        {
            // El usuario puede no existir si el fallo ocurrió antes de crearlo.
        }

        await client.DropDatabaseAsync(dbName, ct);
    }

    public async Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var db = new MongoClient(_adminConnectionString).GetDatabase(dbName);

        // Inverso del vaciado de roles de DeactivateAsync. El comentario de ese
        // método advertía que habría que "recordar el rol/BD original" para
        // poder revertirlo; no hace falta guardarlo en ningún lado porque
        // CreateAsync siempre otorga exactamente el mismo rol: readWrite
        // scoped a la propia BD del estudiante, nunca nada más amplio. Si algún
        // día CreateAsync empieza a otorgar roles variables, este método tiene
        // que dejar de asumirlo y el rol tendrá que persistirse en el catálogo.
        var updateUser = new BsonDocument
        {
            { "updateUser", login },
            { "roles", new BsonArray { new BsonDocument { { "role", "readWrite" }, { "db", dbName } } } }
        };
        await db.RunCommandAsync<BsonDocument>(updateUser, cancellationToken: ct);

        // Este provisioner SÍ aplica la contraseña que recibe, así que no hay
        // nada nuevo que devolver: null significa "quedó vigente la que me
        // pasaste". Ver CredentialRotationResult.
        return null;
    }

    public async Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        var db = new MongoClient(_adminConnectionString).GetDatabase(dbName);

        // dbStats devuelve varias métricas; se usa storageSize (espacio que las
        // colecciones ocupan en disco, ya comprimido) en vez de dataSize (bytes
        // lógicos de los documentos, sin comprimir). storageSize es lo
        // comparable con lo que reportan los otros tres motores y con lo que
        // realmente consume del servidor compartido. Se suma indexSize porque
        // los índices también ocupan y dbStats los deja aparte.
        //
        // Una BD que no existe en Mongo no es un error: dbStats sobre ella
        // responde con ceros, así que el 0 sale solo.
        try
        {
            var stats = await db.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "dbStats", 1 } }, cancellationToken: ct);

            var storage = stats.GetValue("storageSize", 0).ToDouble();
            var indexes = stats.GetValue("indexSize", 0).ToDouble();

            return Math.Round((decimal)((storage + indexes) / 1024d / 1024d), 2);
        }
        catch (MongoException)
        {
            // Motor inalcanzable o BD en un estado inesperado: el job registra
            // el fallo y sigue con las demás, no se aborta el ciclo.
            return 0m;
        }
    }
}
