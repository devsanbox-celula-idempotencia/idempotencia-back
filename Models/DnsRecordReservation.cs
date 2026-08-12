namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) del SP de control <c>sp_ReserveDnsRecord</c>. Mismo
/// patrón que <see cref="DatabaseReservation"/>: el SP valida la cuota, el
/// formato de la etiqueta y de la célula, las etiquetas reservadas y la
/// colisión con otros registros vivos, inserta la fila en
/// <c>Status='Provisioning'</c> y devuelve los valores ya resueltos que el
/// proveedor debe usar.
///
/// El SP compone el <see cref="Fqdn"/> a partir del dominio de zona que le pasa
/// el backend y lo devuelve ya armado. Se persiste compuesto a propósito: a
/// partir de ahí el catálogo es la única fuente del nombre real. Si el backend
/// lo recalculara en cada operación, un cambio de <c>Dns:ZoneName</c> dejaría
/// los registros ya creados apuntando a un nombre distinto del que quedó en
/// Cloudflare, y el borrado no encontraría nada que borrar.
/// </summary>
public class DnsRecordReservation
{
    public int DnsRecordId { get; set; }

    /// <summary>Etiqueta elegida por el usuario, ya normalizada a minúsculas.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Célula (equipo de trabajo) bajo la que cuelga el subdominio.</summary>
    public string Cell { get; set; } = string.Empty;

    /// <summary>Nombre completo del registro: <c>{Label}.{Cell}.{ZoneName}</c>.</summary>
    public string Fqdn { get; set; } = string.Empty;

    /// <summary>Tipo de registro a crear (ver <see cref="DnsRecordType"/>).</summary>
    public string RecordType { get; set; } = string.Empty;

    /// <summary>Destino del registro: para los tipo A, la IPv4 pública del servicio.</summary>
    public string Content { get; set; } = string.Empty;

    public bool Proxied { get; set; }

    /// <summary>TTL en segundos; <c>1</c> = automático (obligatorio si hay proxy).</summary>
    public int Ttl { get; set; }

    /// <summary>
    /// Momento en que quedó la fila en el catálogo. Lo devuelve el SP —y no lo
    /// pone el backend con <c>DateTime.UtcNow</c>— para que la fecha que ve el
    /// usuario en la respuesta sea exactamente la persistida, sin el desfase
    /// (ni el riesgo de zona horaria distinta) de calcularla en otra máquina.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
