namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) de <c>sp_GetDnsRecordDetail</c> y de
/// <c>sp_GetDnsRecordDetailAdmin</c>. A diferencia de
/// <see cref="DnsRecordInfo"/> SÍ incluye <see cref="ProviderRecordId"/>, que el
/// backend necesita internamente para poder actualizar, borrar o revocar el
/// registro en el proveedor. Mismo criterio que
/// <see cref="ProvisionedDatabaseDetail"/> con <c>LoginName</c>: es de uso
/// interno y el controller nunca lo reenvía.
/// </summary>
public class DnsRecordDetail
{
    public int DnsRecordId { get; set; }
    public int UserId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;
    public string Fqdn { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Proxied { get; set; }
    public int Ttl { get; set; }

    /// <summary>
    /// Identificador del registro en el proveedor (Cloudflare: el <c>id</c> de
    /// <c>/zones/{zone}/dns_records</c>). Puede ser <c>null</c> si la fila quedó
    /// en <c>Provisioning</c>/<c>Failed</c> porque la creación nunca llegó a
    /// confirmarse — por eso el proveedor expone también una búsqueda por FQDN,
    /// que permite reconciliar un registro huérfano en vez de dejarlo colgado.
    /// </summary>
    public string? ProviderRecordId { get; set; }

    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
