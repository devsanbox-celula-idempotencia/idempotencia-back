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
    public string Host => _host;
    public int Port => _port;

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

    public async Task ChangePasswordAsync(string dbName, string login, string newPassword, CancellationToken ct = default)
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
    }

    public async Task DeactivateAsync(string dbName, string login, CancellationToken ct = default)
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
