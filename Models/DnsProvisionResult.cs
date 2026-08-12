namespace idempotencia.Models;

/// <summary>
/// Lo que devuelve el proveedor de DNS tras crear el registro. Es el análogo de
/// <see cref="ProvisionResult"/> del aprovisionamiento de bases de datos.
/// </summary>
/// <param name="ProviderRecordId">
/// Identificador del registro en el proveedor. Se persiste en el catálogo al
/// confirmar y es lo que se usa después para actualizarlo o borrarlo.
/// </param>
/// <param name="Fqdn">
/// Nombre completo tal como quedó registrado en el proveedor. Se devuelve el
/// valor que reporta la API (no el que se envió) porque Cloudflare normaliza el
/// nombre — minúsculas y Punycode para dominios con acentos — y el catálogo debe
/// guardar lo que realmente existe.
/// </param>
public record DnsProvisionResult(string ProviderRecordId, string Fqdn);
