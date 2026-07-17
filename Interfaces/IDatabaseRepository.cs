using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Abstracción de acceso a datos de bases de datos aprovisionadas. Las
/// implementaciones SOLO invocan Stored Procedures (validaciones de cuota,
/// límites y creación de login viven en el SP, no en C#).
/// </summary>
public interface IDatabaseRepository
{
    /// <summary>
    /// Invoca <c>sp_CreateDatabase</c>: aprovisiona la BD y devuelve el
    /// identificador junto con las credenciales generadas.
    /// </summary>
    Task<NewDatabaseResult> CreateDatabaseAsync(
        int userId, string dbName, CancellationToken ct = default);

    /// <summary>Invoca <c>sp_GetUserDatabases</c>: lista las BDs del usuario.</summary>
    Task<IReadOnlyList<ProvisionedDatabaseInfo>> GetUserDatabasesAsync(
        int userId, CancellationToken ct = default);
}
