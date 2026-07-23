namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) del SP de control <c>sp_GetDatabaseDetail</c>. A
/// diferencia de <see cref="ProvisionedDatabaseInfo"/> (usado por el listado),
/// SÍ incluye <see cref="LoginName"/> — necesario internamente para que el
/// backend pueda operar sobre el login/usuario físico (desactivar, eliminar,
/// resetear contraseña). El controller nunca reenvía <c>PasswordHash</c> ni
/// nada equivalente a una contraseña en la respuesta HTTP.
/// </summary>
public class ProvisionedDatabaseDetail
{
    public int DatabaseId { get; set; }
    public int UserId { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string DbName { get; set; } = string.Empty;

    /// <summary>Login/usuario físico en el motor — solo se usa internamente, nunca se serializa.</summary>
    public string LoginName { get; set; } = string.Empty;

    public string Status { get; set; } = "Active";
    public int MaxStorageMB { get; set; }
    public decimal CurrentSizeMB { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
