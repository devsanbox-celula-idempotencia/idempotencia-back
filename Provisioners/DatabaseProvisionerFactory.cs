using idempotencia.Interfaces;
using idempotencia.Middleware;

namespace idempotencia.Provisioners;

/// <summary>
/// Resuelve el provisioner correcto según el motor pedido. Recibe TODOS los
/// provisioners registrados en DI y elige por su propiedad <c>Engine</c>.
/// </summary>
public class DatabaseProvisionerFactory : IDatabaseProvisionerFactory
{
    private readonly IEnumerable<IDatabaseProvisioner> _provisioners;

    public DatabaseProvisionerFactory(IEnumerable<IDatabaseProvisioner> provisioners) =>
        _provisioners = provisioners;

    public IDatabaseProvisioner Get(string engine)
    {
        var provisioner = _provisioners.FirstOrDefault(
            p => string.Equals(p.Engine, engine, StringComparison.OrdinalIgnoreCase));

        // Motor desconocido → error controlado 400 (no un 500 genérico).
        return provisioner ?? throw new AppException(
            $"Motor de base de datos no soportado: '{engine}'.",
            StatusCodes.Status400BadRequest);
    }
}
