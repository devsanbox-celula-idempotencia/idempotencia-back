using idempotencia.Interfaces;
using idempotencia.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de MongoDB. Materializa la base creando una colección inicial y
/// registra un usuario con rol readWrite sobre ella. La cuota de tamaño no es
/// nativa por BD, por lo que <paramref name="maxStorageMb"/> no se aplica aquí.
/// </summary>
public class MongoProvisioner : IDatabaseProvisioner
{
    private readonly string _adminConnectionString;
    private readonly string _host;
    private readonly int _port;

    public string Engine => DatabaseEngine.Mongo;

    public MongoProvisioner(IConfiguration config)
    {
        _adminConnectionString = config["Provisioning:Mongo:AdminConnectionString"]
            ?? throw new InvalidOperationException("Falta Provisioning:Mongo:AdminConnectionString.");
        _host = config["Provisioning:Mongo:Host"] ?? "localhost";
        _port = int.TryParse(config["Provisioning:Mongo:Port"], out var p) ? p : 27017;
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

    public async Task DropAsync(string dbName, string login, CancellationToken ct = default)
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
}
