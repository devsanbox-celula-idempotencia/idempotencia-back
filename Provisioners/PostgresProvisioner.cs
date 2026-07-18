using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Provisioners;

/// <summary>
/// STUB (pendiente de implementar). Cuando se aborde:
///   1. Añadir el paquete NuGet <c>Npgsql</c>.
///   2. Conectarse con la cadena de admin (superuser) de PostgreSQL.
///   3. Ejecutar (identificadores citados con comillas dobles):
///        CREATE ROLE "login" LOGIN PASSWORD '...';
///        CREATE DATABASE "dbName" OWNER "login";
///      OJO: CREATE DATABASE en Postgres NO puede ir dentro de una transacción.
///   4. La cuota de tamaño no es nativa: se implementa con tablespaces + límites
///      del SO o cuotas externas (Postgres no tiene un MAXSIZE por BD).
/// </summary>
public class PostgresProvisioner : IDatabaseProvisioner
{
    public string Engine => DatabaseEngine.Postgres;

    public Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de PostgreSQL aún no está implementado.",
            StatusCodes.Status501NotImplemented);

    public Task DropAsync(string dbName, string login, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de PostgreSQL aún no está implementado.",
            StatusCodes.Status501NotImplemented);
}
