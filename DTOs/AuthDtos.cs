using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>Datos de entrada para el registro por contraseña.</summary>
public class RegisterRequest
{
    [Required, EmailAddress, MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [Required, MaxLength(150)]
    public string FullName { get; set; } = string.Empty;
}

/// <summary>Datos de entrada para el login por contraseña.</summary>
public class LoginRequest
{
    [Required, EmailAddress, MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Respuesta estándar de autenticación con el JWT emitido.</summary>
public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = "Student";

    /// <summary>
    /// Credenciales de la BD MySQL aprovisionada automáticamente la primera vez
    /// que este usuario inicia sesión (register/login/OAuth). Solo viene
    /// poblado en el login donde se creó — la contraseña no se puede recuperar
    /// después, igual que en <c>POST /databases</c>. Null si el usuario ya
    /// tenía una BD MySQL o si el aprovisionamiento automático falló (el login
    /// no se bloquea por esto; ver logs del backend).
    /// </summary>
    public ProvisionedDatabaseCredentials? MySqlDatabase { get; set; }
}

/// <summary>
/// Credenciales de conexión de una BD recién aprovisionada, entregadas una
/// única vez (mismo shape que <see cref="CreateDatabaseResponse"/>, para no
/// duplicar significado entre el flujo manual y el automático).
/// </summary>
public class ProvisionedDatabaseCredentials
{
    public int DatabaseId { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string DbName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string LoginName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
