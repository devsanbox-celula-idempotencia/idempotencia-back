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
}
