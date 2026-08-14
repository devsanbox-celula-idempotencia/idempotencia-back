namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) del SP de control <c>sp_GetDatabaseExternalRef</c>:
/// los datos con los que un servicio externo de aprovisionamiento conoce una BD
/// del catálogo.
///
/// Va en un SP aparte y no dentro de <c>sp_GetDatabaseDetail</c> a propósito:
/// ese SP ya lo consumen cuatro flujos y su forma está acoplada al tipo
/// <see cref="ProvisionedDatabaseDetail"/>; agregarle columnas obligaba a
/// tocarlo sin necesidad. El costo es una llamada extra al catálogo en las
/// operaciones de ciclo de vida, que ya hacen al menos una llamada HTTP externa
/// mucho más cara.
/// </summary>
public class ExternalDatabaseRef
{
    /// <summary>
    /// Id con el que el servicio externo identifica la BD. <c>null</c> en toda
    /// base creada por un provisioner local (los cuatro motores originales) y
    /// en las bases de Mongo creadas ANTES de migrar a la API externa.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Nombre físico real de la BD en el motor externo, cuando no coincide con
    /// <c>ProvisionedDatabases.DbName</c> (el que generó <c>sp_ReserveDatabase</c>).
    /// La API de Mongo genera el suyo aleatoriamente, así que este es el nombre
    /// al que el usuario realmente se conecta.
    /// </summary>
    public string? ExternalDbName { get; set; }

    /// <summary>
    /// Usuario real creado por el servicio externo, cuando no coincide con el
    /// <c>LoginName</c> del catálogo. En Mongo coinciden (ese servicio acepta el
    /// nombre que se le manda); en la API de la célula socia de MySQL no, porque
    /// su endpoint de creación no lleva cuerpo y el usuario lo genera ella con su
    /// propio prefijo.
    /// </summary>
    public string? ExternalLoginName { get; set; }

    /// <summary>
    /// Cuota de almacenamiento que aplica REALMENTE el servicio externo, en MB,
    /// cuando la fija él y no el catálogo. Se guarda por base —y no se lee de
    /// configuración cada vez— para que una base creada bajo una cuota siga
    /// reportando la suya si el socio cambia el límite para las nuevas.
    ///
    /// <c>null</c> = manda el <c>MaxStorageMB</c> del catálogo, que es el caso de
    /// todos los motores locales.
    /// </summary>
    public int? ExternalMaxStorageMB { get; set; }

    /// <summary>true si esta BD la administra un servicio externo.</summary>
    public bool IsExternal => !string.IsNullOrWhiteSpace(ExternalId);
}
