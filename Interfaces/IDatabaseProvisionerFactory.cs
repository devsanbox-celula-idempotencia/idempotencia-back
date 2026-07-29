using idempotencia.Interfaces;

namespace idempotencia.Interfaces;

/// <summary>
/// Selector (factory) que resuelve el <see cref="IDatabaseProvisioner"/> adecuado
/// según el motor solicitado. Lanza error controlado si el motor no está soportado.
/// </summary>
public interface IDatabaseProvisionerFactory
{
    IDatabaseProvisioner Get(string engine);
}
