namespace idempotencia.Models;

/// <summary>
/// Tipo de resultado (sin clave) devuelto por el SP <c>sp_GetLoginByEmail</c>.
/// Representa exclusivamente datos de LECTURA; no es una entidad de escritura.
/// El hash de contraseña se verifica en el backend con BCrypt.
/// </summary>
public class LoginInfo
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    /// <summary>Puede ser NULL cuando el usuario entra solo por OAuth.</summary>
    public string? PasswordHash { get; set; }

    public bool IsActive { get; set; }
    public string Role { get; set; } = "Student";
}
