namespace idempotencia.Services;

/// <summary>
/// Configuración del aprovisionamiento de MongoDB delegado en la API externa
/// del equipo, enlazada desde la sección <c>Provisioning:Mongo:Remote</c>.
///
/// A diferencia de los otros tres motores, acá el backend NO habla con un
/// servidor Mongo: habla con un servicio HTTP que crea las bases por él. Por
/// eso no hay <c>AdminConnectionString</c> en esta sección — la conexión
/// administrativa la tiene el servicio, no nosotros.
///
/// <b>La API key no debería vivir en appsettings.json en despliegue.</b> Igual
/// que el token de Cloudflare (ver <see cref="DnsSettings"/>), todas estas
/// claves se pueden sobreescribir por variable de entorno con el separador de
/// doble guion bajo de .NET:
///
/// <code>
/// Provisioning__Mongo__Remote__ApiKey=...
/// Provisioning__Mongo__Remote__Team=Idempotencia
/// </code>
/// </summary>
public class RemoteMongoSettings
{
    public const string SectionName = "Provisioning:Mongo:Remote";

    /// <summary>
    /// Interruptor de la migración. En <c>true</c> el motor "Mongo" se
    /// aprovisiona contra la API externa; en <c>false</c> se vuelve al
    /// <c>MongoProvisioner</c> local con el driver nativo. Existe para poder
    /// revertir sin recompilar si la API externa se cae o cambia el contrato.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// URL base de la API de aprovisionamiento. Debe terminar en <c>/</c>:
    /// <see cref="HttpClient.BaseAddress"/> descarta el último segmento si no
    /// (mismo detalle que ya mordió en la configuración de Cloudflare).
    /// </summary>
    public string BaseUrl { get; set; } = "https://mongo.szapatar.dev/";

    /// <summary>API key de tipo <c>team</c> que autoriza crear/borrar/rotar.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Equipo propietario de las bases. Solo se usa para el fallback de
    /// <c>GET /teams/{team}/databases</c>, que resuelve el id externo de una
    /// base cuando el catálogo no lo tiene guardado.
    /// </summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>
    /// Host público de conexión a Mongo. Es solo un FALLBACK: lo normal es que
    /// el host salga de la <c>connectionString</c> que devuelve la propia API,
    /// que es la fuente de verdad. Esto cubre el caso de una base ya existente
    /// para la que no hay una respuesta fresca a mano.
    /// </summary>
    public string PublicHost { get; set; } = "connection.szapatar.dev";

    /// <summary>Puerto público de conexión. Mismo criterio de fallback que <see cref="PublicHost"/>.</summary>
    public int PublicPort { get; set; } = 27017;

    /// <summary>
    /// Timeout de las llamadas a la API. Corto a propósito: es una dependencia
    /// externa dentro del ciclo de un request del usuario, y fallar rápido para
    /// revertir la reserva es mejor que colgar la petición hasta el timeout por
    /// defecto de 100 segundos de <see cref="HttpClient"/>.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}
