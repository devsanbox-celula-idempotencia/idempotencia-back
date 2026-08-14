namespace idempotencia.Models;

/// <summary>
/// Tipo de resultado (sin clave) de <c>sp_GetUserDnsRecords</c>. Representa un
/// subdominio del usuario, en solo lectura. Es el análogo de
/// <see cref="ProvisionedDatabaseInfo"/> para el listado de DNS.
///
/// No incluye <see cref="DnsRecordDetail.ProviderRecordId"/>: ese identificador
/// es interno del proveedor y no le sirve de nada al frontend.
/// </summary>
public class DnsRecordInfo
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
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
