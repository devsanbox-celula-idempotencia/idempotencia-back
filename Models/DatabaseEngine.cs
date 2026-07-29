namespace idempotencia.Models;

/// <summary>
/// Identificadores de los motores soportados por Colmena. Se usan como clave
/// para resolver el provisioner correcto (patrón Strategy) y como valor a
/// persistir en la columna Engine de ProvisionedDatabases.
/// </summary>
public static class DatabaseEngine
{
    public const string SqlServer = "SqlServer";
    public const string Postgres = "Postgres";
    public const string MySql = "MySql";
    public const string Mongo = "Mongo";
}
