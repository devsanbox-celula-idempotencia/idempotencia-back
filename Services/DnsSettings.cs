namespace idempotencia.Services;

/// <summary>
/// Configuración del servicio de DNS, enlazada desde la sección <c>Dns</c> de
/// <c>appsettings.json</c> / secrets / variables de entorno.
///
/// Los subdominios que los usuarios crean para sus proyectos siguen la
/// estructura de tres niveles <c>{label}.{celula}.{ZoneName}</c> — p. ej.
/// <c>airflow.datos.coderhivex.com</c>, donde <c>airflow</c> lo elige el
/// usuario y <c>datos</c> es su célula (equipo de trabajo).
///
/// <b>El token no debería vivir en appsettings.json en despliegue.</b> Todas
/// estas claves se pueden sobreescribir por variable de entorno con el
/// separador de doble guion bajo de .NET, que es la forma recomendada:
///
/// <code>
/// Dns__ApiToken=cfut_xxxxxxxx
/// Dns__ZoneId=c1c62663d28fa916dc9bc030103e6e83
/// Dns__ZoneName=coderhivex.com
/// </code>
/// </summary>
public class DnsSettings
{
    public const string SectionName = "Dns";

    /// <summary>
    /// Proveedor de DNS activo. Hoy solo existe <c>"Cloudflare"</c>; la clave
    /// está para que agregar otro sea configuración y no un cambio de código en
    /// el resto del flujo.
    /// </summary>
    public string Provider { get; set; } = "Cloudflare";

    /// <summary>
    /// URL base de la API del proveedor. Configurable para poder apuntar a un
    /// mock en pruebas sin recompilar. Debe terminar en <c>/</c>:
    /// <see cref="HttpClient.BaseAddress"/> descarta el último segmento si no.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api.cloudflare.com/client/v4/";

    /// <summary>Identificador de la zona en el proveedor (Cloudflare: Zone ID).</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Token de API con permiso <c>Zone.DNS: Edit</c> sobre <see cref="ZoneId"/>.
    /// Es un secreto: nunca se loguea ni se devuelve en ninguna respuesta HTTP.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Dominio de la zona, sin punto final. Ej: <c>coderhivex.com</c>.</summary>
    public string ZoneName { get; set; } = string.Empty;

    /// <summary>
    /// Célula que se usa cuando el cliente no manda ninguna. Hoy el despliegue
    /// tiene una sola célula (<c>idempotencia</c>), así que pedirle al frontend
    /// que la repita en cada request sería obligarlo a hardcodear un valor que
    /// ya vive en la configuración del backend — exactamente el tipo de dato que
    /// después queda desincronizado cuando cambia.
    ///
    /// El campo <c>cell</c> del request sigue existiendo y sigue teniendo
    /// prioridad: el día que haya varias células no hay que tocar el contrato,
    /// solo empezar a mandarlo.
    /// </summary>
    public string DefaultCell { get; set; } = "idempotencia";

    /// <summary>
    /// Tipo de registro que se crea. Fijo en <c>A</c>: el usuario da la IPv4
    /// pública donde corre su servicio. No se expone como opción del cliente —
    /// permitir CNAME sería otro flujo de validación (destino como hostname,
    /// con sus propios riesgos de apuntar a infraestructura ajena).
    /// </summary>
    public string RecordType { get; set; } = "A";

    /// <summary>
    /// Proxy de Cloudflare (nube naranja). Va en <c>true</c> y NO es
    /// configurable por el usuario, porque de esto depende el HTTPS.
    ///
    /// El motivo es la profundidad del nombre: el comodín gratuito de Universal
    /// SSL cubre <c>*.{ZoneName}</c>, que es UN solo nivel, y estos subdominios
    /// tienen dos (<c>{label}.{celula}.{ZoneName}</c>). Quien emite el
    /// certificado para ese nombre es <b>Total TLS</b> (parte de Advanced
    /// Certificate Manager), y Total TLS solo emite certificados para hostnames
    /// <b>proxeados</b>. Un registro sin proxy en este esquema queda resolviendo
    /// pero sin HTTPS válido.
    ///
    /// Consecuencia a tener presente: al pasar por el proxy, el subdominio solo
    /// enruta HTTP y HTTPS. No sirve para exponer un puerto TCP arbitrario.
    /// </summary>
    public bool Proxied { get; set; } = true;

    /// <summary>
    /// TTL en segundos. Con <see cref="Proxied"/> en <c>true</c> Cloudflare
    /// exige <c>1</c> ("automático"): al pasar por el borde, el TTL lo gobierna
    /// el proxy y no el registro.
    /// </summary>
    public int TtlSeconds { get; set; } = 1;

    /// <summary>
    /// Timeout de las llamadas HTTP al proveedor. Corto a propósito: es una
    /// dependencia externa dentro del ciclo de un request del usuario, y es
    /// preferible fallar rápido (y revertir la reserva) que dejar la petición
    /// colgada.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Arma el nombre completo del registro. <paramref name="label"/> y
    /// <paramref name="cell"/> ya vienen validados y normalizados a minúsculas
    /// por el DTO; acá solo se concatenan.
    /// </summary>
    public string BuildFqdn(string label, string cell) => $"{label}.{cell}.{ZoneName}";
}
