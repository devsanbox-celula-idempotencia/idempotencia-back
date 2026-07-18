namespace idempotencia.Models;

/// <summary>
/// Datos de conexión que devuelve un provisioner tras crear físicamente la BD
/// en su motor. Sirven para que el estudiante sepa a dónde conectarse.
/// </summary>
public record ProvisionResult(string Host, int Port);
