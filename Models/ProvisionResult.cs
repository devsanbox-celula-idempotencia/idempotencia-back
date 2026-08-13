namespace idempotencia.Models;

/// <summary>
/// Datos de conexión que devuelve un provisioner tras crear físicamente la BD
/// en su motor. Sirven para que el estudiante sepa a dónde conectarse.
///
/// Los campos posteriores a <paramref name="Port"/> son opcionales y existen
/// para los provisioners que NO controlan la creación: cuando la BD la crea un
/// servicio externo (ver <see cref="idempotencia.Provisioners.RemoteMongoProvisioner"/>),
/// el nombre físico y la contraseña los decide ese servicio, no el backend, así
/// que el provisioner tiene que poder devolver lo que realmente quedó creado.
/// En los cuatro provisioners "clásicos" (motor local, driver nativo) van todos
/// en <c>null</c>: el backend ya sabe lo que pidió porque él mismo lo generó.
/// </summary>
/// <param name="Host">Host público al que se conecta el usuario final.</param>
/// <param name="Port">Puerto público del motor.</param>
/// <param name="ExternalId">
/// Identificador con el que el servicio externo conoce esta BD. Es lo ÚNICO
/// que permite volver a operarla después (borrarla, rotar credenciales), así
/// que el orquestador lo persiste en el catálogo apenas la creación confirma.
/// <c>null</c> en provisioners locales, que direccionan por nombre.
/// </param>
/// <param name="EffectiveDbName">
/// Nombre físico REAL de la BD, cuando difiere del que reservó el catálogo. La
/// API de Mongo genera un nombre aleatorio e independiente del que se le pide,
/// así que sin esto la cadena de conexión que se le entrega al usuario
/// apuntaría a una base que no existe.
/// </param>
/// <param name="EffectivePassword">
/// Contraseña REAL con la que quedó creado el usuario, cuando no es la que
/// generó el backend. El servicio externo genera la suya y no acepta una
/// impuesta; el orquestador guarda el hash de ESTA y es la que entrega al
/// usuario.
/// </param>
/// <param name="ConnectionUri">
/// Cadena de conexión tal como la devuelve el servicio externo. Se prefiere
/// sobre la que arma <c>BuildClientConnection</c> porque es la única que se
/// sabe con certeza correcta: la construye quien creó la base.
/// </param>
public record ProvisionResult(
    string Host,
    int Port,
    string? ExternalId = null,
    string? EffectiveDbName = null,
    string? EffectivePassword = null,
    string? ConnectionUri = null);
