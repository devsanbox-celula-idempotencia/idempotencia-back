using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace idempotencia.Provisioners;

/// <summary>
/// Provisioner de MongoDB que NO habla con un servidor Mongo: delega la
/// creación en la API de aprovisionamiento del equipo
/// (<c>https://mongo.szapatar.dev</c>), autenticándose con una API key de tipo
/// <c>team</c>.
///
/// Vive junto a los provisioners locales y junto a <see cref="CloudflareDnsProvider"/>
/// porque cumple el mismo rol arquitectónico que este último: es un adaptador
/// hacia un sistema externo. Se registra como <c>HttpClient</c> tipado (ver
/// <c>Program.cs</c>) por la misma razón que aquel — handler reutilizado y
/// reciclado solo, sin agotar sockets ni quedarse pegado a una IP vieja.
///
/// <b>Tres cosas que esta API decide y el backend no</b>, y que son el origen de
/// casi todo lo raro de esta clase:
///
/// <list type="number">
///   <item>
///     <b>El nombre físico de la base.</b> La API genera uno aleatorio e
///     independiente del <c>username</c> que se le manda, así que el nombre que
///     reservó <c>sp_ReserveDatabase</c> NO es el nombre real. Se devuelve en
///     <see cref="ProvisionResult.EffectiveDbName"/> y el orquestador lo
///     persiste: sin eso, la cadena de conexión del usuario apunta a una base
///     inexistente.
///   </item>
///   <item>
///     <b>La contraseña.</b> La genera la API y la devuelve; no acepta una
///     impuesta. <c>PasswordGenerator</c> deja de mandar en este motor y lo que
///     se hashea en el catálogo es la de ella
///     (<see cref="ProvisionResult.EffectivePassword"/>).
///   </item>
///   <item>
///     <b>Cómo se direcciona la base después.</b> Los endpoints de borrado y
///     rotación son por <c>id</c>, no por nombre. Ese id se guarda en el
///     catálogo al crear y vuelve por el parámetro <c>externalId</c> de cada
///     método del ciclo de vida.
///   </item>
/// </list>
///
/// <b>Lo que la API no ofrece</b> y esta clase tiene que emular o admitir que no
/// puede: no existe desactivar/reactivar (se emulan rotando credenciales, ver
/// <see cref="DeactivateAsync"/>) ni una medición de tamaño por base (ver
/// <see cref="GetSizeMbAsync"/>).
/// </summary>
public class RemoteMongoProvisioner : IDatabaseProvisioner
{
    private readonly HttpClient _http;
    private readonly RemoteMongoSettings _settings;
    private readonly ILogger<RemoteMongoProvisioner> _logger;

    public string Engine => DatabaseEngine.Mongo;

    /// <summary>
    /// Host público de fallback. A diferencia de los provisioners locales —donde
    /// el host es un dato de configuración y punto— acá la fuente de verdad es
    /// la <c>connectionString</c> que devuelve la API en cada operación. Esta
    /// propiedad cubre las lecturas en las que no hay una respuesta fresca a
    /// mano (el <c>GET /databases/{id}</c> del catálogo, por ejemplo).
    /// </summary>
    public string Host => _settings.PublicHost;

    /// <inheritdoc cref="Host" />
    public int Port => _settings.PublicPort;

    public RemoteMongoProvisioner(
        HttpClient http,
        IOptions<RemoteMongoSettings> settings,
        ILogger<RemoteMongoProvisioner> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ClientConnectionInfo BuildClientConnection(string dbName, string login, string password)
    {
        // Se replica EXACTAMENTE el formato que devuelve la API
        // (mongodb://usuario:clave@host:puerto/base) en vez de agregarle
        // authSource como hace el provisioner local. No es un olvido: en una URI
        // mongodb:// el authSource por defecto ES la base que va en la ruta, así
        // que para un usuario creado dentro de su propia base el parámetro es
        // redundante. Agregarlo "por si acaso" haría que esta cadena difiera de
        // la que la propia API entrega, y ante una discrepancia el usuario no
        // sabría a cuál hacerle caso.
        //
        // Este método es el camino de RESPALDO: cuando hay una respuesta fresca
        // de la API (crear, rotar) el orquestador prefiere su connectionString,
        // que es la única que se sabe correcta por construcción.
        //
        // Mongo no tiene un driver JDBC estándar (los clientes usan el driver
        // nativo o mongosh), así que solo se entrega la URI.
        return new ClientConnectionInfo(
            $"mongodb://{Uri.EscapeDataString(login)}:{Uri.EscapeDataString(password)}" +
            $"@{Host}:{Port}/{dbName}",
            null);
    }

    public async Task<ProvisionResult> CreateAsync(
        string dbName, string login, string password, int maxStorageMb,
        int maxConcurrentConnections, CancellationToken ct = default)
    {
        // maxStorageMb y maxConcurrentConnections se ignoran: la API no expone
        // ninguna de las dos cuotas. Es la misma situación que ya tenía el
        // provisioner local de Mongo (docs/bugs.md ítem 12), solo que ahora
        // tampoco podríamos aplicarlas por fuera aunque quisiéramos.
        //
        // password TAMBIÉN se ignora — la API genera la suya. Se recibe igual
        // porque la firma es común a los cinco provisioners, y devolverla en
        // EffectivePassword es justamente la forma de avisar que la buena es
        // otra.
        //
        // Se manda el login que generó el catálogo como "username": la API
        // permite repetirlo entre bases (cada una es independiente), así que no
        // hay riesgo de colisión, y mantiene la trazabilidad entre lo que ve el
        // usuario en Colmena y lo que ve el equipo en su panel.
        using var response = await _http.PostAsJsonAsync(
            "databases", new CreateDatabaseBody(login), ct);

        var created = await ReadAsync<RemoteDatabaseResponse>(
            response, $"crear la base de datos MongoDB para '{login}'", ct);

        if (string.IsNullOrWhiteSpace(created.Id) || string.IsNullOrWhiteSpace(created.Database))
        {
            // Sin id no hay forma de volver a operar la base (ni de borrarla),
            // así que se trata como fallo: el orquestador revierte la reserva.
            // Es preferible a confirmar en el catálogo una base huérfana que
            // nadie podrá eliminar después.
            throw new AppException(
                "La API de MongoDB respondió sin 'id' o sin 'database'; no se puede " +
                "registrar la base en el catálogo.",
                StatusCodes.Status502BadGateway);
        }

        var (host, port) = ParseEndpoint(created.ConnectionString);

        _logger.LogInformation(
            "Base MongoDB creada en la API externa: id={ExternalId}, database={Database}, username={Username}.",
            created.Id, created.Database, created.Username);

        return new ProvisionResult(
            host, port,
            ExternalId: created.Id,
            EffectiveDbName: created.Database,
            EffectivePassword: created.Password,
            ConnectionUri: created.ConnectionString);
    }

    public async Task DropAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        var id = await ResolveExternalIdAsync(externalId, dbName, ct);

        if (id is null)
        {
            // Puede pasar legítimamente en la reversión de una creación que
            // falló ANTES de que la API llegara a crear nada: no hay id porque
            // no hay base. Se registra y se sigue — lanzar acá convertiría el
            // error original en uno peor y menos informativo.
            _logger.LogWarning(
                "No se encontró id externo para la base '{DbName}': no hay nada que borrar en la API.",
                dbName);
            return;
        }

        using var response = await _http.DeleteAsync($"databases/{Uri.EscapeDataString(id)}", ct);

        // 404 = la base ya no existe en la API. Para un borrado eso es el
        // resultado deseado, no un error: permite reintentar el DELETE del
        // usuario sin que se quede atascado por un estado que ya es el correcto.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "La API reportó 404 al borrar la base {ExternalId} ('{DbName}'): ya no existía.",
                id, dbName);
            return;
        }

        await EnsureSuccessAsync(response, $"eliminar la base de datos MongoDB '{dbName}'", ct);

        _logger.LogInformation("Base MongoDB {ExternalId} ('{DbName}') eliminada en la API externa.", id, dbName);
    }

    public async Task<CredentialRotationResult?> ChangePasswordAsync(
        string dbName, string login, string newPassword, string? externalId,
        CancellationToken ct = default)
    {
        // newPassword se ignora: la API genera la suya y no acepta una impuesta.
        // Se devuelve la que quedó realmente vigente para que el orquestador
        // hashee ESA y sea ESA la que le llegue al usuario por correo.
        var rotated = await RotateCredentialsAsync(dbName, externalId, ct);

        return new CredentialRotationResult(rotated.Password, rotated.ConnectionString);
    }

    public async Task DeactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        // La API no tiene "desactivar". Se emula rotando las credenciales y
        // DESCARTANDO la contraseña nueva: nadie la conoce —ni el usuario ni el
        // backend, que solo guarda hashes— así que la credencial vieja deja de
        // servir y la base queda inalcanzable. Los datos no se tocan, que es la
        // garantía que el endpoint promete.
        //
        // Diferencia real frente a los otros motores, que conviene tener clara:
        // allá desactivar es reversible SIN costo (el usuario vuelve con la
        // misma contraseña); acá reactivar obliga a emitir una contraseña nueva
        // y mandarla por correo, porque la anterior es irrecuperable por diseño.
        var rotated = await RotateCredentialsAsync(dbName, externalId, ct);

        _logger.LogInformation(
            "Base MongoDB '{DbName}' desactivada rotando su credencial en la API externa " +
            "(la contraseña nueva se descarta a propósito). Rotada el {RotatedAt}.",
            dbName, rotated.RotatedAt);
    }

    public async Task<CredentialRotationResult?> ReactivateAsync(
        string dbName, string login, string? externalId, CancellationToken ct = default)
    {
        // Inverso de DeactivateAsync dentro de lo que la API permite: se rota
        // otra vez y esta vez SÍ se devuelve la contraseña, para que el
        // orquestador la persista (hash) y se la envíe al usuario.
        //
        // No es idempotente en el mismo sentido que los otros cuatro motores
        // —cada llamada emite una contraseña distinta e invalida la anterior—,
        // pero sí es seguro reintentarlo: el estado final siempre es "la base
        // es alcanzable con la última contraseña enviada".
        var rotated = await RotateCredentialsAsync(dbName, externalId, ct);

        return new CredentialRotationResult(rotated.Password, rotated.ConnectionString);
    }

    public Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct = default)
    {
        // La API no expone el tamaño de una base. GET /admin/stats devuelve
        // conteos agregados por equipo (cuántas activas/eliminadas), no bytes, y
        // además exige la API key admin, que este backend no tiene.
        //
        // Se devuelve -1 ("no medible"), NO 0: cero afirmaría que la base está
        // vacía y el job lo persistiría, borrando el último tamaño conocido y
        // haciendo que la API de Colmena reporte 0.00 MB para bases con datos.
        // Ver la convención documentada en IDatabaseProvisioner.GetSizeMbAsync.
        return Task.FromResult(-1m);
    }

    /// <summary>
    /// Llama al endpoint de rotación de credenciales, que es el único primitivo
    /// que la API ofrece para cambiar el acceso a una base ya creada. Los tres
    /// métodos que lo usan (cambiar contraseña, desactivar, reactivar) se
    /// diferencian solo en qué hacen con el resultado.
    /// </summary>
    private async Task<(string Password, string? ConnectionString, DateTime? RotatedAt)> RotateCredentialsAsync(
        string dbName, string? externalId, CancellationToken ct)
    {
        var id = await ResolveExternalIdAsync(externalId, dbName, ct)
            ?? throw new AppException(
                $"La base de datos '{dbName}' no tiene un identificador en la API de MongoDB, " +
                "así que no se pueden rotar sus credenciales. Si se creó antes de la migración " +
                "a la API externa, hay que operarla con el proveedor anterior.",
                StatusCodes.Status409Conflict);

        using var response = await _http.PostAsync(
            $"databases/{Uri.EscapeDataString(id)}/credentials/reset", content: null, ct);

        var rotated = await ReadAsync<RemoteDatabaseResponse>(
            response, $"rotar las credenciales de la base de datos MongoDB '{dbName}'", ct);

        if (string.IsNullOrWhiteSpace(rotated.Password))
        {
            // Escenario venenoso si se dejara pasar: la API ya rotó (la
            // credencial vieja murió) pero no sabemos la nueva. Se falla fuerte
            // para que el usuario reintente y reciba una que sí conocemos, en
            // vez de guardar en el catálogo un hash de algo que no es la
            // contraseña real.
            throw new AppException(
                "La API de MongoDB rotó las credenciales pero no devolvió la contraseña nueva. " +
                "Vuelve a intentarlo para obtener una credencial válida.",
                StatusCodes.Status502BadGateway);
        }

        // La tupla existe para que el compilador —y quien lea a los tres
        // llamadores— sepa que la contraseña ya está validada como no nula.
        return (rotated.Password, rotated.ConnectionString, rotated.RotatedAt);
    }

    /// <summary>
    /// Devuelve el id con el que la API conoce esta base. Normalmente ya viene
    /// del catálogo; el listado por equipo es un plan B para las filas que no lo
    /// tengan guardado —una creación que confirmó en la API pero falló al
    /// persistir la referencia, o cualquier base tocada antes de que existiera
    /// la columna—. Sin este respaldo esas bases quedarían imposibles de borrar
    /// desde Colmena.
    /// </summary>
    private async Task<string?> ResolveExternalIdAsync(
        string? externalId, string dbName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(externalId))
            return externalId;

        if (string.IsNullOrWhiteSpace(_settings.Team))
        {
            _logger.LogWarning(
                "No hay id externo para '{DbName}' y Provisioning:Mongo:Remote:Team no está " +
                "configurado, así que no se puede resolver por el listado del equipo.", dbName);
            return null;
        }

        using var response = await _http.GetAsync(
            $"teams/{Uri.EscapeDataString(_settings.Team)}/databases", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "No se pudo listar las bases del equipo {Team} para resolver '{DbName}': HTTP {Status}.",
                _settings.Team, dbName, (int)response.StatusCode);
            return null;
        }

        var listing = await response.Content.ReadFromJsonAsync<RemoteTeamDatabases>(ct);

        // Se busca por nombre FÍSICO, que es único por base en la API; el
        // username no sirve como llave porque la propia documentación permite
        // repetirlo entre bases distintas.
        var match = listing?.Databases?.FirstOrDefault(d =>
            string.Equals(d.Database, dbName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Status, "deleted", StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _logger.LogWarning(
                "La base '{DbName}' no aparece activa en el listado del equipo {Team}.",
                dbName, _settings.Team);
        }

        return match?.Id;
    }

    /// <summary>
    /// Saca host y puerto de la cadena de conexión que devuelve la API. Se
    /// prefiere a la configuración porque es el dato que el servicio realmente
    /// está entregando; la configuración queda como red de seguridad si la
    /// cadena viniera vacía o con una forma inesperada (por ejemplo
    /// <c>mongodb+srv://</c>, donde no hay puerto explícito).
    /// </summary>
    private (string Host, int Port) ParseEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (Host, Port);

        try
        {
            var url = MongoUrl.Create(connectionString);
            var server = url.Servers.FirstOrDefault();

            if (server is null || string.IsNullOrWhiteSpace(server.Host))
                return (Host, Port);

            return (server.Host, server.Port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No se pudo interpretar la cadena de conexión que devolvió la API de MongoDB; " +
                "se reporta el host/puerto de configuración ({Host}:{Port}).", Host, Port);

            return (Host, Port);
        }
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
                $"La API de MongoDB devolvió un cuerpo vacío al {accion}.",
                StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Traduce un fallo HTTP de la API a un <see cref="AppException"/> con un
    /// código que signifique algo para el usuario final.
    ///
    /// Todo lo que no sea 401/403 se reporta como 502: el usuario no hizo nada
    /// mal, falló una dependencia nuestra, y un 500 genérico haría parecer que
    /// el bug está en Colmena. 401/403 se dejan como 502 también —y no como 401—
    /// a propósito: son un problema de configuración de NUESTRA API key, no de
    /// la sesión del usuario, y devolverle un 401 lo mandaría a reautenticarse
    /// sin motivo. Se distinguen solo en el mensaje y en el log.
    /// </summary>
    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, string accion, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await SafeReadBodyAsync(response, ct);
        var status = (int)response.StatusCode;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "La API de MongoDB rechazó la API key al {Accion}: HTTP {Status}. Cuerpo: {Body}",
                accion, status, body);

            throw new AppException(
                "El servicio de MongoDB rechazó las credenciales del backend. " +
                "Revisa Provisioning:Mongo:Remote:ApiKey.",
                StatusCodes.Status502BadGateway);
        }

        _logger.LogError(
            "Fallo al {Accion} contra la API de MongoDB: HTTP {Status}. Cuerpo: {Body}",
            accion, status, body);

        throw new AppException(
            $"El servicio de MongoDB respondió con un error ({status}) al {accion}.",
            StatusCodes.Status502BadGateway);
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
    // Contratos de la API externa. Son privados y anidados a propósito: no son
    // modelos del dominio de Colmena, son la forma exacta de un servicio ajeno,
    // y sacarlos de acá invitaría a usarlos como si fueran nuestros.
    // ---------------------------------------------------------------------

    private record CreateDatabaseBody(
        [property: JsonPropertyName("username")] string Username);

    /// <summary>
    /// Cuerpo que devuelven tanto <c>POST /databases</c> como
    /// <c>POST /databases/{id}/credentials/reset</c>. Se usa un solo tipo
    /// porque los campos que importan son los mismos; los que cada endpoint no
    /// manda quedan en <c>null</c> (la rotación no devuelve <c>id</c>, la
    /// creación no devuelve <c>rotatedAt</c>).
    /// </summary>
    private record RemoteDatabaseResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("database")] string? Database,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("connectionString")] string? ConnectionString,
        [property: JsonPropertyName("createdAt")] DateTime? CreatedAt,
        [property: JsonPropertyName("rotatedAt")] DateTime? RotatedAt);

    private record RemoteTeamDatabases(
        [property: JsonPropertyName("databases")] List<RemoteTeamDatabase>? Databases);

    private record RemoteTeamDatabase(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("database")] string? Database,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("team")] string? Team,
        [property: JsonPropertyName("status")] string? Status);
}
