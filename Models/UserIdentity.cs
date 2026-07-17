namespace idempotencia.Models;

/// <summary>
/// Identidad resuelta de un usuario devuelta por los SPs de
/// <c>sp_RegisterUser</c> y <c>sp_UpsertExternalLogin</c>.
/// Contiene únicamente lo necesario para firmar el JWT.
/// </summary>
public class UserIdentity
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = "Student";
}
