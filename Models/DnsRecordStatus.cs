namespace idempotencia.Models;

/// <summary>
/// Estados del ciclo de vida de un registro DNS en el catálogo. Los mismos
/// valores que acepta la constraint <c>CK_DnsRecords_Status</c> — si se agrega
/// uno acá hay que agregarlo también allá, o el SP falla con un 547 (es
/// exactamente el bug 24 del catálogo de bases de datos, ver docs/bugs.md).
///
/// Flujo normal: <see cref="Provisioning"/> (reservado, todavía no existe en
/// Cloudflare) → <see cref="Active"/> (creado y confirmado) o
/// <see cref="Failed"/> (la API del proveedor falló y se revirtió).
///
/// Hay DOS estados terminales distintos a propósito, y la diferencia es quién
/// actuó: <see cref="Deleted"/> es el usuario eliminando su propio subdominio;
/// <see cref="Revoked"/> es el equipo quitándoselo (inactividad, abuso). Podrían
/// colapsarse en uno solo, pero entonces una auditoría no podría responder
/// "¿cuántos subdominios revocamos este mes?" sin cruzar tablas de logs que no
/// existen. Los dos liberan el nombre por igual.
/// </summary>
public static class DnsRecordStatus
{
    public const string Provisioning = "Provisioning";
    public const string Active = "Active";
    public const string Failed = "Failed";
    public const string Deleted = "Deleted";
    public const string Revoked = "Revoked";
}
