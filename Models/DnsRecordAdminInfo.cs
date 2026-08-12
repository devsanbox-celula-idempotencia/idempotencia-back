namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) de <c>sp_GetAllDnsRecords</c>, el listado de
/// administración. Es un tipo aparte de <see cref="DnsRecordInfo"/> y no una
/// extensión suya por una razón concreta de EF Core: los tipos sin clave se
/// mapean por los nombres de columna del conjunto de resultados, así que un tipo
/// con columnas que un SP no devuelve quedaría con esas propiedades en su valor
/// por defecto, en silencio. Dos SPs con columnas distintas = dos tipos.
///
/// Lo que agrega respecto del listado del usuario es lo que hace falta para
/// auditar: a quién pertenece cada registro (<see cref="UserEmail"/>) y cuánto
/// hace que nadie lo toca (<see cref="DaysSinceUpdate"/>), que es el insumo del
/// criterio de revocación por inactividad.
/// </summary>
public class DnsRecordAdminInfo
{
    public int DnsRecordId { get; set; }
    public int UserId { get; set; }

    /// <summary>
    /// Correo del dueño, resuelto por el SP con un JOIN contra la tabla de
    /// usuarios. Se incluye para que auditar no obligue a una segunda consulta
    /// por cada fila del listado.
    /// </summary>
    public string UserEmail { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;
    public string Fqdn { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Proxied { get; set; }
    public int Ttl { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Días transcurridos desde la última modificación. Lo calcula el SP y no el
    /// backend para que el filtro por antigüedad y el valor que se muestra usen
    /// exactamente el mismo reloj — el del servidor de base de datos.
    /// </summary>
    public int DaysSinceUpdate { get; set; }
}
