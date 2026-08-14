namespace idempotencia.Services;

/// <summary>
/// Configuración del aprovisionamiento de MySQL delegado en la API de la célula
/// socia, enlazada desde la sección <c>Provisioning:MySql:Remote</c>.
///
/// A diferencia de los motores locales, acá el backend NO habla con un servidor
/// MySQL: habla con un servicio HTTP que crea las bases por él. Por eso no hay
/// <c>AdminConnectionString</c> en esta sección — la conexión administrativa la
/// tiene el socio, no nosotros.
///
/// <b>La API key no debería vivir en appsettings.json en despliegue.</b> El
/// propio documento del socio lo dice sin rodeos: quien la tenga puede crear y
/// eliminar bases a nombre de nuestra célula. Igual que el token de Cloudflare
/// (ver <see cref="DnsSettings"/>), se puede sobreescribir por variable de
/// entorno con el separador de doble guion bajo de .NET:
///
/// <code>
/// Provisioning__MySql__Remote__ApiKey=...
/// </code>
/// </summary>
public class RemoteMySqlSettings
{
    public const string SectionName = "Provisioning:MySql:Remote";

    /// <summary>
    /// Interruptor de la migración. En <c>true</c> el motor "MySql" se
    /// aprovisiona contra la API de la célula socia; en <c>false</c> se vuelve
    /// al <c>MySqlProvisioner</c> local con el driver nativo. Existe para poder
    /// revertir sin recompilar si esa API se cae o cambia el contrato.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// URL base de la API del socio. Debe terminar en <c>/</c>:
    /// <see cref="HttpClient.BaseAddress"/> descarta el último segmento si no
    /// (mismo detalle que ya mordió en la configuración de Cloudflare).
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.aba.andrescortes.dev/";

    /// <summary>
    /// API key de la célula. Viaja como <c>Authorization: Bearer</c>, que es el
    /// esquema que exige el socio — no confundir con el JWT que este backend le
    /// emite a SUS usuarios: son dos credenciales distintas en direcciones
    /// opuestas y solo comparten el nombre del header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Host público de conexión a MySQL que se le entrega al estudiante. Sale de
    /// configuración y no de la respuesta de creación para que el detalle de una
    /// base ya existente pueda reportarlo sin gastar una llamada HTTP — ver la
    /// nota sobre el rate limit en <see cref="RequestsPerHourHint"/>.
    /// </summary>
    public string PublicHost { get; set; } = "db.aba.andrescortes.dev";

    /// <summary>Puerto público de conexión. Mismo criterio que <see cref="PublicHost"/>.</summary>
    public int PublicPort { get; set; } = 3306;

    /// <summary>
    /// Cuota de almacenamiento por base que aplica el socio, en MB. Se persiste
    /// por base al crear (<c>ExternalMaxStorageMB</c>) y es la que se le reporta
    /// al usuario, porque es la que realmente va a pausar su base.
    ///
    /// Sale de configuración y no de la API por el rate limit: la respuesta de
    /// creación NO trae la cuota, así que averiguarla obligaría a un
    /// <c>GET /partners/databases/{id}</c> extra por cada base creada —el doble
    /// de consumo en el camino más caliente que hay, el alta automática de cada
    /// usuario nuevo por OAuth—. Si el socio cambia el límite, se actualiza esta
    /// clave y las bases nuevas lo toman; las viejas conservan el suyo, que es
    /// el comportamiento correcto.
    /// </summary>
    public int MaxStorageMB { get; set; } = 20;

    /// <summary>
    /// Exige TLS en las cadenas de conexión que se le entregan al estudiante
    /// (<c>ssl-mode=REQUIRED</c> / <c>sslMode=REQUIRED</c>).
    ///
    /// Arranca APAGADO a propósito, al revés que el MySQL propio: no controlamos
    /// ese servidor ni sabemos si tiene TLS habilitado, y pedir REQUIRED contra
    /// un motor sin certificado deja al usuario sin poder conectarse. Con la
    /// clave apagada no se emite el parámetro y el driver aplica su propio
    /// default (PREFERRED: cifra si el servidor puede, y si no, sigue).
    ///
    /// Súbelo a <c>true</c> apenas el equipo socio confirme que el motor expone
    /// TLS: con el canal cifrado, el intercambio de clave de
    /// <c>caching_sha2_password</c> ocurre dentro del túnel y el usuario se
    /// ahorra el paso manual de <c>allowPublicKeyRetrieval</c> (docs/bugs.md
    /// ítem 28).
    /// </summary>
    public bool RequireTls { get; set; }

    /// <summary>
    /// Timeout de las llamadas a la API. Corto a propósito: es una dependencia
    /// externa dentro del ciclo de un request del usuario, y fallar rápido para
    /// revertir la reserva es mejor que colgar la petición hasta el timeout por
    /// defecto de 100 segundos de <see cref="HttpClient"/>.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Solo documental — no lo lee ningún código. Deja constancia del
    /// presupuesto real con el que hay que diseñar cualquier llamada nueva a
    /// esta API: el socio permite 10 requests de ráfaga con recarga de 1 cada 2
    /// minutos POR CÉLULA (no por IP, no por usuario), o sea unos 30 por hora en
    /// régimen. Cada alta de usuario nuevo por OAuth ya consume uno.
    ///
    /// Es la razón de dos decisiones que de otro modo parecerían arbitrarias:
    /// <c>GetSizeMbAsync</c> no mide (medir cada base cada 15 minutos agotaría la
    /// cuota y devolvería 429 a los usuarios reales) y la cuota de almacenamiento
    /// sale de configuración en vez de consultarse por base.
    /// </summary>
    public int RequestsPerHourHint { get; set; } = 30;
}
