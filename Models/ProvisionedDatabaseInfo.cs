namespace idempotencia.Models;

/// <summary>
/// Tipo de resultado (sin clave) devuelto por <c>sp_GetUserDatabases</c>.
/// Representa una base de datos aprovisionada del usuario (solo lectura).
/// </summary>
public class ProvisionedDatabaseInfo
{
    public int DatabaseId { get; set; }
    public int UserId { get; set; }

    /// <summary>Motor de la BD (nueva columna Engine en ProvisionedDatabases).</summary>
    public string Engine { get; set; } = string.Empty;

    public string DbName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int MaxStorageMB { get; set; }
    public decimal CurrentSizeMB { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
