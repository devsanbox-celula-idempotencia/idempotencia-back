using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>
/// Regex compartido por <c>Label</c> y <c>Cell</c>: la sintaxis de una etiqueta
/// DNS del RFC 1035 restringida al juego LDH (letras, dígitos, guion). Empieza y
/// termina en alfanumérico, admite guiones en el medio, entre 3 y 63 caracteres.
/// </summary>
internal static class DnsLabelPattern
{
    public const string Value = @"^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])$";
}

/// <summary>
/// Datos de entrada para crear un subdominio. El nombre final se arma como
/// <c>{label}.{cell}.{Dns:ZoneName}</c> — p. ej.
/// <c>airflow.datos.coderhivex.com</c>. El sufijo de zona lo pone el backend,
/// así que nadie puede pedir un nombre fuera del dominio de la plataforma.
/// </summary>
public class CreateDnsRecordRequest : IValidatableObject
{
    private string _label = string.Empty;
    private string? _cell;
    private string _ipAddress = string.Empty;

    /// <summary>
    /// Nombre del subdominio que elige el usuario: lo que va antes de la célula.
    /// En el ejemplo del requisito, <c>airflow</c>.
    ///
    /// Se prohíbe el punto a propósito: un label con punto agregaría un nivel más
    /// de profundidad al nombre, y cada nivel extra es un certificado más que
    /// Total TLS tiene que emitir por separado — el subdominio existiría pero el
    /// HTTPS podría no estar listo cuando el usuario lo visite.
    ///
    /// Se normaliza a minúsculas porque el DNS es case-insensitive: sin esto,
    /// "Airflow" y "airflow" pasarían la validación de unicidad del catálogo como
    /// dos etiquetas distintas y la segunda creación fallaría recién en
    /// Cloudflare, con un error mucho menos claro.
    ///
    /// La lista de etiquetas reservadas (www, api, admin, mail…) NO se valida
    /// acá sino en <c>sp_ReserveDnsRecord</c>: es una regla de negocio y este
    /// proyecto las mantiene en la base, no en el DTO.
    /// </summary>
    [Required(ErrorMessage = "El nombre del subdominio es obligatorio.")]
    [MaxLength(63)]
    [RegularExpression(DnsLabelPattern.Value,
        ErrorMessage = "El nombre solo puede contener letras, números y guiones, debe empezar y terminar con letra o número, y tener entre 3 y 63 caracteres.")]
    public string Label
    {
        get => _label;
        // ToLowerInvariant y no ToLower: la cultura del servidor no debe influir
        // en cómo se normaliza un nombre de dominio (el caso clásico es la "I"
        // del turco, que en su cultura baja a "ı" y produciría un label distinto).
        set => _label = InputNormalization.TrimOrEmpty(value).ToLowerInvariant();
    }

    /// <summary>
    /// Célula (equipo de trabajo) bajo la que cuelga el subdominio: el nivel
    /// intermedio de <c>airflow.{celula}.coderhivex.com</c>.
    ///
    /// <b>Opcional.</b> Si se omite se usa <c>Dns:DefaultCell</c>
    /// (<c>idempotencia</c>), que es la única célula del despliegue actual. El
    /// campo existe igual para que el día que haya varias no haya que cambiar el
    /// contrato, solo empezar a mandarlo.
    ///
    /// <b>Se valida el formato, no la pertenencia.</b> No existe todavía un
    /// catálogo de células ni una relación usuario↔célula en la base, así que
    /// cualquier usuario autenticado puede crear un subdominio bajo el nombre de
    /// cualquier célula. Es una decisión consciente para no bloquear esta
    /// entrega, pero <b>no es el estado final</b>: mientras siga así, el control
    /// real es a posteriori (el listado y la revocación de <c>/admin/dns</c>,
    /// que filtran por célula justamente para esto). Cuando exista el catálogo,
    /// la validación de pertenencia va en <c>sp_ReserveDnsRecord</c> y nada de
    /// acá cambia.
    /// </summary>
    [MaxLength(63)]
    [RegularExpression(DnsLabelPattern.Value,
        ErrorMessage = "La célula solo puede contener letras, números y guiones, debe empezar y terminar con letra o número, y tener entre 3 y 63 caracteres.")]
    public string? Cell
    {
        get => _cell;
        set
        {
            var normalized = InputNormalization.TrimOrEmpty(value).ToLowerInvariant();
            // Cadena vacía y ausencia significan lo mismo —"usá la célula por
            // defecto"— así que se colapsan a null. Además evita que el
            // [RegularExpression] rechace un "" que el usuario nunca escribió:
            // los atributos de validación ignoran null, pero no la cadena vacía.
            _cell = normalized.Length == 0 ? null : normalized;
        }
    }

    /// <summary>
    /// IPv4 pública del servidor donde corre el servicio del usuario. Se crea un
    /// registro de tipo A apuntando a esta dirección.
    ///
    /// El formato se valida en <see cref="Validate"/> y no con un
    /// <c>[RegularExpression]</c>, porque un regex puede comprobar la forma pero
    /// no que la dirección sea alcanzable desde internet — ver
    /// <see cref="IpAddressRules"/>.
    /// </summary>
    [Required(ErrorMessage = "La dirección IP es obligatoria.")]
    [MaxLength(15)]
    public string IpAddress
    {
        get => _ipAddress;
        set => _ipAddress = InputNormalization.TrimOrEmpty(value);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IpAddressRules.IsPublicIpv4(IpAddress))
        {
            yield return new ValidationResult(
                "La dirección IP debe ser una IPv4 pública y alcanzable desde internet " +
                "(no se admiten rangos privados, de loopback ni de red interna).",
                new[] { nameof(IpAddress) });
        }
    }
}

/// <summary>
/// Datos de entrada para reapuntar un subdominio existente a otra IP.
///
/// Deliberadamente NO expone <c>proxied</c> ni <c>ttl</c>. Ambos los fija la
/// plataforma y de ellos depende que el HTTPS funcione: sin proxy, Total TLS no
/// emite el certificado de un nombre de dos niveles, y el subdominio queda
/// resolviendo sin HTTPS válido. Dejar que el usuario los apague sería darle un
/// botón para romper su propio sitio sin entender por qué.
/// </summary>
public class UpdateDnsRecordRequest : IValidatableObject
{
    private string _ipAddress = string.Empty;

    /// <summary>Nueva IPv4 pública de destino.</summary>
    [Required(ErrorMessage = "La dirección IP es obligatoria.")]
    [MaxLength(15)]
    public string IpAddress
    {
        get => _ipAddress;
        set => _ipAddress = InputNormalization.TrimOrEmpty(value);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IpAddressRules.IsPublicIpv4(IpAddress))
        {
            yield return new ValidationResult(
                "La dirección IP debe ser una IPv4 pública y alcanzable desde internet " +
                "(no se admiten rangos privados, de loopback ni de red interna).",
                new[] { nameof(IpAddress) });
        }
    }
}

/// <summary>
/// Representación de lectura de un subdominio. No incluye el identificador
/// interno del proveedor: es un detalle de implementación de Cloudflare que no le
/// aporta nada al frontend y que, si se expusiera, ataría el contrato de la API a
/// un proveedor concreto.
/// </summary>
public class DnsRecordResponse
{
    public int DnsRecordId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;

    /// <summary>Nombre completo listo para usar: <c>{label}.{cell}.coderhivex.com</c>.</summary>
    public string Fqdn { get; set; } = string.Empty;

    public string RecordType { get; set; } = string.Empty;

    /// <summary>IPv4 de destino (el <c>content</c> del registro A).</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Si el tráfico pasa por el proxy de Cloudflare. Se expone en la respuesta
    /// aunque el usuario no pueda cambiarlo, porque explica dos cosas que sí va a
    /// notar: que el HTTPS es automático y que solo se enruta HTTP/HTTPS.
    /// </summary>
    public bool Proxied { get; set; }

    public int Ttl { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Fila del listado de administración (<c>GET /admin/dns</c>). Agrega lo que el
/// equipo necesita para auditar y que un usuario no debe ver de los demás: de
/// quién es cada registro y hace cuánto que nadie lo toca.
/// </summary>
public class AdminDnsRecordResponse : DnsRecordResponse
{
    public int UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;

    /// <summary>
    /// Días desde la última modificación. Es el insumo del criterio de
    /// revocación por inactividad; lo calcula la base para que el filtro y el
    /// valor mostrado usen el mismo reloj.
    /// </summary>
    public int DaysSinceUpdate { get; set; }
}

/// <summary>
/// Motivo de una revocación administrativa. Es obligatorio a propósito: la
/// revocación se guarda en el catálogo como registro de auditoría, y una
/// auditoría que dice "revocado" sin decir por qué no sirve para nada seis meses
/// después. El costo de escribir una línea lo paga el operador una vez; el costo
/// de no tenerla lo paga quien investigue.
/// </summary>
public class RevokeDnsRecordRequest
{
    private string _reason = string.Empty;

    [Required(ErrorMessage = "El motivo de la revocación es obligatorio.")]
    [MinLength(10, ErrorMessage = "El motivo debe tener al menos 10 caracteres.")]
    [MaxLength(500)]
    public string Reason
    {
        get => _reason;
        set => _reason = InputNormalization.CollapseSpaces(value);
    }
}
