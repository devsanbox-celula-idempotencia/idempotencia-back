using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Provisioners;

/// <summary>
/// STUB (pendiente de implementar). MongoDB NO es SQL. Cuando se aborde:
///   1. Añadir el paquete NuGet <c>MongoDB.Driver</c>.
///   2. Conectarse con un usuario admin del clúster.
///   3. Mongo crea la BD de forma perezosa: hay que "materializarla" creando una
///      colección inicial y luego el usuario con roles:
///        db.createCollection("_init")
///        db.runCommand({ createUser: "login", pwd: "...",
///                        roles: [{ role: "readWrite", db: "dbName" }] })
///   4. La cuota se controla con límites del clúster o monitoreo (no hay MAXSIZE).
/// </summary>
public class MongoProvisioner : IDatabaseProvisioner
{
    public string Engine => DatabaseEngine.Mongo;

    public Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de MongoDB aún no está implementado.",
            StatusCodes.Status501NotImplemented);

    public Task DropAsync(string dbName, string login, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de MongoDB aún no está implementado.",
            StatusCodes.Status501NotImplemented);
}
