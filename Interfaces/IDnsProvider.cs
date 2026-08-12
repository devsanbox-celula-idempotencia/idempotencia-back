using idempotencia.Models;

namespace idempotencia.Interfaces;

/// <summary>
/// Adaptador contra UN proveedor de DNS concreto (hoy Cloudflare). Cumple el
/// mismo papel que <see cref="IDatabaseProvisioner"/> en el aprovisionamiento de
/// bases de datos: es la única parte del flujo que no puede vivir en un Stored
/// Procedure, porque habla HTTP contra un sistema externo.
///
/// A diferencia de <see cref="IDatabaseProvisioner"/> acá NO hay factory: los
/// motores de base de datos son cuatro y conviven simultáneamente (el usuario
/// elige uno por BD), mientras que el proveedor de DNS es uno solo por
/// despliegue y se elige por configuración. Meter un factory para una sola
/// implementación sería indirección sin beneficio; la interfaz sí existe para
/// que agregar Route53 mañana no obligue a tocar el servicio orquestador.
/// </summary>
public interface IDnsProvider
{
    /// <summary>Nombre del proveedor que implementa esta clase (<c>Dns:Provider</c>).</summary>
    string Provider { get; }

    /// <summary>
    /// Dominio de la zona que administra este proveedor (<c>Dns:ZoneName</c>,
    /// p. ej. <c>coderhivex.com</c>). Se expone para que el controller pueda
    /// informarlo al frontend sin volver a leer configuración.
    /// </summary>
    string ZoneName { get; }

    /// <summary>
    /// Crea el registro en la zona. Devuelve el identificador que asignó el
    /// proveedor, necesario para actualizarlo o borrarlo después.
    ///
    /// Si el proveedor responde que el registro YA existe, se traduce a una
    /// <see cref="Middleware.AppException"/> 409 en vez de un 500: es una
    /// colisión de nombre, un error del usuario, no una falla del sistema.
    /// </summary>
    Task<DnsProvisionResult> CreateAsync(
        string fqdn, string recordType, string content, bool proxied, int ttl,
        CancellationToken ct = default);

    /// <summary>
    /// Actualiza el destino/TTL/proxy de un registro existente sin cambiar su
    /// nombre. Se usa un PUT con el registro completo (y no un PATCH parcial)
    /// porque el catálogo ya es la fuente de verdad de todos los campos: enviar
    /// el estado completo evita que una diferencia entre catálogo y proveedor
    /// sobreviva a la actualización.
    /// </summary>
    Task UpdateAsync(
        string providerRecordId, string fqdn, string recordType, string content,
        bool proxied, int ttl, CancellationToken ct = default);

    /// <summary>
    /// Borra el registro en el proveedor. Es idempotente: si el registro ya no
    /// existe (404 del proveedor) NO lanza, porque el estado final buscado —"ese
    /// nombre no resuelve"— ya se cumple, y lanzar dejaría al usuario sin poder
    /// limpiar una fila del catálogo cuyo registro alguien borró a mano desde el
    /// panel de Cloudflare.
    /// </summary>
    Task DeleteAsync(string providerRecordId, CancellationToken ct = default);

    /// <summary>
    /// Busca el identificador de un registro por su nombre completo. Existe para
    /// reconciliar el caso feo del flujo de creación: el proveedor creó el
    /// registro pero la confirmación en el catálogo falló, así que la fila quedó
    /// en <c>Provisioning</c> con <c>ProviderRecordId = NULL</c> y el registro
    /// quedó huérfano en la zona. Con esto, el borrado puede encontrarlo por
    /// nombre y limpiarlo igual. Devuelve <c>null</c> si no existe.
    /// </summary>
    Task<string?> FindRecordIdAsync(string fqdn, CancellationToken ct = default);
}
