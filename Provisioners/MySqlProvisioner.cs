using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;

namespace idempotencia.Provisioners;

/// <summary>
/// STUB (pendiente de implementar). Cuando se aborde:
///   1. Añadir el paquete NuGet <c>MySqlConnector</c>.
///   2. Conectarse con la cadena de admin (root) de MySQL/MariaDB.
///   3. Ejecutar (identificadores con backticks, valores como parámetros):
///        CREATE DATABASE `dbName`;
///        CREATE USER 'login'@'%' IDENTIFIED BY '...';
///        GRANT ALL PRIVILEGES ON `dbName`.* TO 'login'@'%';
///   4. La cuota de tamaño no es nativa por BD: se controla con monitoreo o
///      límites a nivel de tablespace/SO.
/// </summary>
public class MySqlProvisioner : IDatabaseProvisioner
{
    public string Engine => DatabaseEngine.MySql;

    public Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de MySQL/MariaDB aún no está implementado.",
            StatusCodes.Status501NotImplemented);

    public Task DropAsync(string dbName, string login, CancellationToken ct = default) =>
        throw new AppException(
            "El aprovisionamiento de MySQL/MariaDB aún no está implementado.",
            StatusCodes.Status501NotImplemented);
}
