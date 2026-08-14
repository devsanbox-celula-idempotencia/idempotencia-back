using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Extensions.Options;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de MySQL que NO habla con un servidor MySQL: delega la creación
/// en la API de la célula socia (<c>https://api.aba.andrescortes.dev</c>),
/// autenticándose con la API key de nuestra célula.
///
/// Mismo rol arquitectónico que <see cref="RemoteMongoProvisioner"/> y
/// <see cref="CloudflareDnsProvider"/>: un adaptador hacia un sistema externo,
/// registrado como <c>HttpClient</c> tipado (ver <c>Program.cs</c>) para que el
/// handler se reutilice y se recicle solo.
///
/// <b>Lo que esta API decide y el backend no.</b> Es aún más restrictiva que la
/// de Mongo: su <c>POST /partners/databases</c> <b>no lleva cuerpo</b>, así que
/// no se puede proponer ni el nombre de la base ni el del usuario. Los genera
/// ella con el prefijo de la célula, junto con la contraseña, y devuelve un
/// <c>baseDeDatosId</c> que es la única forma de volver a operar la base. Los
/// tres viajan de vuelta en <see cref="ProvisionResult"/> y el orquestador los
/// persiste.
///
/// <b>El rate limit manda sobre el diseño.</b> El socio permite 10 requests de
/// ráfaga con recarga de 1 cada 2 minutos POR CÉLULA — unos 30 por hora para
/// todo el backend, compartidos entre crear, eliminar y rotar, y con cada alta
/// por OAuth consumiendo uno. De ahí salen dos decisiones que sin ese contexto
/// parecerían pereza:
///
/// <list type="bullet">
///   <item>
///     <see cref="GetSizeMbAsync"/> NO mide, aunque la API sí expone el tamaño.
///     El <c>DatabaseSizeMonitor</c> recorre todas las bases activas cada 15
///     minutos: medir una por una agotaría la cuota en el primer ciclo y a
///     partir de ahí los usuarios reales recibirían 429 al crear.
///   </item>
///   <item>
///     La cuota de almacenamiento sale de configuración
///     (<c>Provisioning:MySql:Remote:MaxStorageMB</c>) y no de la API: la
///     respuesta de creación no la trae, y consultarla costaría una llamada
///     extra por cada base creada.
///   </item>
/// </list>
///
/// <b>Lo que la API no ofrece:</b> no existe desactivar/reactivar (se emulan
/// rotando credenciales, ver <see cref="DeactivateAsync"/>). Sí tiene un estado
/// <c>PAUSADA</c>, pero lo aplica y lo quita ella sola cuando una base pasa o
/// baja de su cuota — no es una operación que se pueda invocar, y el catálogo de
/// Colmena no lo refleja (ver la nota de <see cref="DropAsync"/> sobre los 404).
/// </summary>
public class RemoteMySqlProvisioner : IDatabaseProvisioner
{
    private readonly HttpClient _http;
    private readonly RemoteMySqlSettings _settings;
    private readonly ILogger<RemoteMySqlProvisioner> _logger;

    public string Engine => DatabaseEngine.MySql;

    /// <summary>
    /// Host público de conexión. Sale de configuración y no de la respuesta de
    /// la API —aunque ella lo devuelva en cada operación— porque el detalle de
    /// una base ya existente tiene que poder reportarlo sin gastar una llamada
    /// del presupuesto de rate limit.
    /// </summary>
    public string Host => _settings.PublicHost;

    /// <inheritdoc cref="Host" />
    public int Port => _settings.PublicPort;

    public RemoteMySqlProvisioner(
        HttpClient http,
        IOptions<RemoteMySqlSettings> settings,
        ILogger<RemoteMySqlProvisioner> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ClientConnectionInfo BuildClientConnection(string dbName, string login, string password)
    {
        // ssl-mode (driver nativo / cliente de consola) y sslMode (JDBC) son la
        // MISMA opción escrita distinto por cada driver — de ahí que se entreguen
        // las dos cadenas ya armadas. Igual que en el provisioner local, pero
        // apagado por defecto: no controlamos el servidor del socio y exigir TLS
        // contra un motor sin certificado deja al usuario sin conectarse. Con la
        // clave apagada no se emite el parámetro y cada driver aplica su default
        // (PREFERRED), que cifra si puede y sigue si no.
        var uriTls = _settings.RequireTls ? "?ssl-mode=REQUIRED" : string.Empty;
        var jdbcTls = _settings.RequireTls ? "?sslMode=REQUIRED" : string.Empty;

        return new ClientConnectionInfo(
            $"mysql://{Uri.EscapeDataString(login)}:{Uri.EscapeDataString(password)}" +
            $"@{Host}:{Port}/{dbName}{uriTls}",
            $"jdbc:mysql://{Host}:{Port}/{dbName}{jdbcTls}");
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        // Los cinco parámetros se ignoran, y no por descuido: el endpoint del
        // socio no acepta cuerpo. Nombre, usuario y contraseña los genera él;
        // la cuota de almacenamiento la fija él (20 MB) y el tope de conexiones
        // concurrentes no lo expone. Se reciben igual porque la firma es común a
        // los seis provisioners, y devolver los valores reales en el
        // ProvisionResult es justamente la forma de avisar cuáles quedaron.
        using var response = await _http.PostAsync("partners/databases", content: null, ct);

        var created = await ReadAsync<PartnerDatabaseResponse>(
            response, "crear la base de datos MySQL", ct);

        if (created.BaseDeDatosId is null || string.IsNullOrWhiteSpace(created.NombreBD))
        {
            // Sin id no hay forma de volver a operar la base (ni de borrarla),
            // así que se trata como fallo y el orquestador revierte la reserva.
            // Es preferible a confirmar en el catálogo una base huérfana que
            // consume la cuota de la célula y que nadie podrá eliminar. Es
            // exactamente el error que el propio documento del socio marca como
            // el más común al integrar: guardar las credenciales y perder el id.
            throw new AppException(
                "La API de MySQL respondió sin 'baseDeDatosId' o sin 'nombreBD'; no se " +
                "puede registrar la base en el catálogo.",
                StatusCodes.Status502BadGateway);
        }

        _logger.LogInformation(
            "Base MySQL creada en la API de la célula socia: id={ExternalId}, nombreBD={Database}, " +
            "usuarioBD={Username}.",
            created.BaseDeDatosId, created.NombreBD, created.UsuarioBD);

        return new ProvisionResult(
            // Host y puerto se reportan desde configuración, no desde la
            // respuesta: así el detalle de una base ya existente coincide con lo
            // que se entregó al crear, sin tener que volver a preguntar.
            Host, Port,
            ExternalId: created.BaseDeDatosId.Value.ToString(),
            EffectiveDbName: created.NombreBD,
            EffectiveLogin: created.UsuarioBD,
            EffectivePassword: created.PasswordTemporal,
            // La cuota sale de configuración y no de la respuesta —que no la
            // trae— para no gastar una llamada extra por cada base creada. Se
            // persiste por base: si el socio sube el límite, las nuevas lo toman
            // y las viejas conservan el que tenían.
            ExternalMaxStorageMB: _settings.MaxStorageMB);
    }

    public async Task DropAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        if (externalId is null)
        {
            // Puede pasar legítimamente al revertir una creación que falló ANTES
            // de que la API llegara a crear nada. A diferencia de Mongo, acá NO
            // hay plan B: el listado del socio (GET /partners/databases) devuelve
            // todas las bases de la célula pero nada que las vincule a un usuario
            // del catálogo, así que buscar por nombre sería adivinar. Se registra
            // y se sigue: lanzar convertiría el error original en uno peor.
            _logger.LogWarning(
                "No hay id externo para la base MySQL '{DbName}': no hay nada que borrar en la API.",
                dbName);
            return;
        }

        using var response = await _http.DeleteAsync(
            $"partners/databases/{Uri.EscapeDataString(externalId)}", ct);

        // 404 = no existe, no es nuestra, o ya estaba borrada. Para un borrado
        // eso es el resultado deseado, no un error: deja reintentar el DELETE del
        // usuario sin que se quede atascado por un estado que ya es el correcto.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "La API respondió 404 al borrar la base MySQL {ExternalId} ('{DbName}'): ya no existía.",
                externalId, dbName);
            return;
        }

        await EnsureSuccessAsync(response, $"eliminar la base de datos MySQL '{dbName}'", ct);

        _logger.LogInformation(
            "Base MySQL {ExternalId} ('{DbName}') eliminada en la API de la célula socia.",
            externalId, dbName);
    }

    public async Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default)
    {
        // newPassword se ignora: la API genera la suya y no acepta una impuesta.
        // Se devuelve la que quedó realmente vigente para que el orquestador
        // hashee ESA y sea ESA la que le llegue al usuario por correo.
        var rotated = await RotateCredentialsAsync(dbName, externalId, ct);

        return new CredentialRotationResult(rotated, null);
    }

    public async Task DeactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        // La API no tiene "desactivar" invocable. Se emula igual que en Mongo:
        // se rota la credencial y se DESCARTA la contraseña nueva. Nadie la
        // conoce —ni el usuario ni el backend, que solo guarda hashes— así que
        // la credencial vieja deja de servir y la base queda inalcanzable, con
        // los datos intactos, que es la garantía que promete el endpoint.
        //
        // Ojo con el estado PAUSADA del socio: es otra cosa y no se toca acá.
        // Ese lo pone y lo quita él solo según la cuota de espacio, y mientras
        // dura, su endpoint de rotación responde 404 — ver RotateCredentialsAsync.
        await RotateCredentialsAsync(dbName, externalId, ct);

        _logger.LogInformation(
            "Base MySQL '{DbName}' desactivada rotando su credencial en la API de la célula socia " +
            "(la contraseña nueva se descarta a propósito).", dbName);
    }

    public async Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        // Inverso de DeactivateAsync dentro de lo que la API permite: se rota
        // otra vez y esta vez SÍ se devuelve la contraseña, para que el
        // orquestador la persista (hash) y se la envíe al usuario por correo.
        //
        // No es idempotente en el mismo sentido que los motores locales —cada
        // llamada emite una contraseña distinta e invalida la anterior— pero sí
        // es seguro reintentarlo: el estado final siempre es "la base es
        // alcanzable con la última contraseña enviada".
        var rotated = await RotateCredentialsAsync(dbName, externalId, ct);

        return new CredentialRotationResult(rotated, null);
    }

    public Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        // La API SÍ expone el tamaño (espacioUtilizadoMB en
        // GET /partners/databases/{id} y en el listado), pero medir desde acá es
        // inviable con su rate limit: 10 de ráfaga y 1 cada 2 minutos POR CÉLULA.
        // El DatabaseSizeMonitor recorre todas las bases activas cada 15 minutos,
        // así que con más de un puñado de bases el primer ciclo agota la cuota y
        // a partir de ahí los usuarios reales reciben 429 al crear su base. Medir
        // el tamaño no vale ese precio.
        //
        // Se devuelve -1 ("no medible"), NO 0: cero afirmaría que la base está
        // vacía y el job lo persistiría, borrando el último valor conocido. Ver
        // la convención en IDatabaseProvisioner.GetSizeMbAsync.
        //
        // Si alguna vez hace falta el dato, la forma correcta NO es medir una por
        // una: es un snapshot de GET /partners/databases (una sola llamada por
        // ciclo) cacheado en memoria y compartido por todas las mediciones del
        // ciclo. Queda anotado en el backlog de docs/claude.md.
        return Task.FromResult(-1m);
    }

    /// <summary>
    /// Llama al endpoint de rotación de credenciales, único primitivo que la API
    /// ofrece para cambiar el acceso a una base ya creada. Los tres métodos que
    /// lo usan (cambiar contraseña, desactivar, reactivar) se diferencian solo en
    /// qué hacen con la contraseña que devuelve.
    /// </summary>
    private async Task<string> RotateCredentialsAsync(
        string dbName, string? externalId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new AppException(
                $"La base de datos '{dbName}' no tiene un identificador en la API de MySQL, " +
                "así que no se pueden rotar sus credenciales. Si se creó antes de la migración " +
                "a la API de la célula socia, hay que operarla con el proveedor anterior.",
                StatusCodes.Status409Conflict);
        }

        using var response = await _http.PostAsync(
            $"partners/databases/{Uri.EscapeDataString(externalId)}/credenciales/reset",
            content: null, ct);

        // El 404 de este endpoint significa tres cosas distintas —no existe, no
        // es nuestra, o NO ESTÁ ACTIVA— y la tercera es la que confunde: incluye
        // el caso de una base PAUSADA por el socio, que se pausa sola al superar
        // su cuota de espacio. Desde el catálogo esa base se ve 'Active', así que
        // sin este mensaje el usuario recibiría un "no encontrada" sobre una base
        // que está viendo en pantalla.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "La API respondió 404 al rotar credenciales de la base MySQL {ExternalId} " +
                "('{DbName}'): no existe, no es de esta célula, o no está ACTIVA (posible " +
                "PAUSADA por cuota de espacio).", externalId, dbName);

            throw new NotFoundException(
                "No se pudo cambiar la contraseña de esta base de datos. Puede que el servicio " +
                "la haya pausado por superar su cuota de espacio: libera espacio y vuelve a " +
                "intentarlo en unos minutos.");
        }

        var rotated = await ReadAsync<PartnerDatabaseResponse>(
            response, $"rotar las credenciales de la base de datos MySQL '{dbName}'", ct);

        if (string.IsNullOrWhiteSpace(rotated.PasswordNueva))
        {
            // Escenario venenoso si se dejara pasar: la API ya rotó —su propio
            // documento avisa que la contraseña vieja deja de servir apenas
            // responde 200— pero no sabemos la nueva. Se falla fuerte para que el
            // usuario reintente y reciba una que sí conocemos, en vez de guardar
            // en el catálogo el hash de algo que no es la contraseña real.
            throw new AppException(
                "La API de MySQL rotó las credenciales pero no devolvió la contraseña nueva. " +
                "Vuelve a intentarlo para obtener una credencial válida.",
                StatusCodes.Status502BadGateway);
        }

        return rotated.PasswordNueva;
    }

    /// <summary>
    /// Lee el cuerpo JSON de una respuesta exitosa, o traduce el fallo a una
    /// excepción de la aplicación.
    /// </summary>
    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response, string accion, CancellationToken ct)
        where T : class
    {
        await EnsureSuccessAsync(response, accion, ct);

        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new AppException(
                $"La API de MySQL devolvió un cuerpo vacío al {accion}.",
                StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Traduce un fallo HTTP del socio a un <see cref="AppException"/> con un
    /// código que signifique algo para NUESTRO usuario final, que no sabe que
    /// existe una célula socia detrás.
    ///
    /// El criterio en una línea: se propaga el código solo cuando describe algo
    /// que le pasa al usuario; cuando describe algo que nos pasa a nosotros
    /// (credenciales del backend, cuota de la célula, fallo del motor ajeno) se
    /// traduce a 502/503, porque un 401 o un 409 crudos le harían creer que el
    /// problema es suyo.
    /// </summary>
    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, string accion, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await SafeReadBodyAsync(response, ct);
        var status = (int)response.StatusCode;

        switch (response.StatusCode)
        {
            // Nuestra API key es inválida o está desactivada. Es un problema de
            // configuración del backend, no de la sesión del usuario: devolverle
            // un 401 lo mandaría a reautenticarse sin motivo.
            case HttpStatusCode.Unauthorized:
                _logger.LogError(
                    "La API de la célula socia rechazó la API key al {Accion}: HTTP {Status}. Cuerpo: {Body}",
                    accion, status, body);

                throw new AppException(
                    "El servicio de MySQL rechazó las credenciales del backend. " +
                    "Revisa Provisioning:MySql:Remote:ApiKey.",
                    StatusCodes.Status502BadGateway);

            // Se agotó el límite de bases activas de TODA la célula (hoy 500).
            // El usuario no puede hacer nada al respecto, pero tampoco es un
            // fallo transitorio: se le dice que no hay cupo, y el log queda para
            // que alguien pida ampliación al socio.
            case HttpStatusCode.Conflict:
                _logger.LogError(
                    "La célula alcanzó el límite de bases MySQL activas al {Accion}. Cuerpo: {Body}",
                    accion, body);

                throw new AppException(
                    "No hay cupo para crear más bases de datos MySQL en este momento. " +
                    "Contacta al equipo de la plataforma.",
                    StatusCodes.Status503ServiceUnavailable);

            // Rate limit del socio: 10 de ráfaga, 1 cada 2 minutos, por célula.
            // Es el único código que se propaga tal cual, porque "esperá y
            // reintentá" es exactamente la acción correcta — aunque el cupo se
            // haya consumido por culpa de otro usuario, no del que recibe el 429.
            case HttpStatusCode.TooManyRequests:
                _logger.LogWarning(
                    "Rate limit de la API de la célula socia agotado al {Accion}. Cuerpo: {Body}",
                    accion, body);

                throw new AppException(
                    "El servicio de MySQL está recibiendo demasiadas solicitudes en este momento. " +
                    "Espera un par de minutos y vuelve a intentarlo.",
                    StatusCodes.Status429TooManyRequests);

            // 422 (fallo de aprovisionamiento de su lado) y 503 (el motor MySQL
            // falló): su documento marca los dos como reintentables.
            case HttpStatusCode.UnprocessableEntity:
            case HttpStatusCode.ServiceUnavailable:
                _logger.LogError(
                    "Fallo transitorio de la API de la célula socia al {Accion}: HTTP {Status}. Cuerpo: {Body}",
                    accion, status, body);

                throw new AppException(
                    "El servicio de MySQL no pudo completar la operación en este momento. " +
                    "Vuelve a intentarlo en unos minutos.",
                    StatusCodes.Status503ServiceUnavailable);

            default:
                _logger.LogError(
                    "Fallo al {Accion} contra la API de la célula socia: HTTP {Status}. Cuerpo: {Body}",
                    accion, status, body);

                throw new AppException(
                    $"El servicio de MySQL respondió con un error ({status}) al {accion}.",
                    StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Lee el cuerpo de una respuesta fallida solo para el log. Nunca lanza: ya
    /// estamos en el camino de error y una excepción acá taparía la causa real.
    /// </summary>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 500 ? body[..500] + "…" : body;
        }
        catch
        {
            return "(no se pudo leer)";
        }
    }

    // ---------------------------------------------------------------------
    // Contrato de la API socia. Privado y anidado a propósito: no es un modelo
    // del dominio de Colmena, es la forma exacta de un servicio ajeno (en
    // español, además, a diferencia del resto del código), y sacarlo de acá
    // invitaría a usarlo como si fuera nuestro.
    //
    // Un solo tipo para las respuestas de crear y de rotar: comparten los campos
    // que importan y difieren solo en el nombre de la contraseña
    // (passwordTemporal vs. passwordNueva) y en que la rotación no devuelve
    // 'motor'. Lo que un endpoint no manda queda en null.
    // ---------------------------------------------------------------------

    private record PartnerDatabaseResponse(
        [property: JsonPropertyName("baseDeDatosId")] int? BaseDeDatosId,
        [property: JsonPropertyName("nombreBD")] string? NombreBD,
        [property: JsonPropertyName("usuarioBD")] string? UsuarioBD,
        [property: JsonPropertyName("passwordTemporal")] string? PasswordTemporal,
        [property: JsonPropertyName("passwordNueva")] string? PasswordNueva,
        [property: JsonPropertyName("host")] string? HostName,
        [property: JsonPropertyName("puerto")] int? Puerto,
        [property: JsonPropertyName("motor")] string? Motor);
}
