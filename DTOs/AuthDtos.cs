using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>Datos de entrada para el registro por contraseña.</summary>
public class RegisterRequest
{
    private string _email = string.Empty;
    private string _fullName = string.Empty;

    // [EmailAddress] ya valida el formato general; el [RegularExpression]
    // adicional es más estricto a propósito (exige exactamente un "@" y un
    // "." en el dominio, sin espacios) — defensa en profundidad ante formatos
    // "técnicamente válidos" pero claramente mal escritos (ej. "a@b@c",
    // "a@b."). El valor ya llega trimeado/en minúsculas por el setter de
    // abajo, así que un correo con mayúsculas o espacios de más no genera un
    // 400 innecesario ni crea una cuenta "distinta" por una diferencia
    // cosmética.
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "El correo no tiene un formato válido.")]
    [RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "El correo no tiene un formato válido.")]
    [MaxLength(150)]
    public string Email
    {
        get => _email;
        set => _email = InputNormalization.NormalizeEmail(value);
    }

    // A propósito NO se trimea/normaliza: un espacio en la contraseña puede
    // ser intencional y parte de la clave real del usuario.
    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [MinLength(8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    // Letras (incluye acentos/ñ vía \p{L}), espacios, apóstrofes, guiones y
    // puntos — cubre nombres compuestos y apellidos con partícula sin admitir
    // dígitos ni símbolos de control/inyección. Rechaza un nombre de un solo
    // carácter o vacío tras el trim.
    [Required(ErrorMessage = "El nombre completo es obligatorio.")]
    [MaxLength(150)]
    [RegularExpression(@"^[\p{L}\p{M}][\p{L}\p{M} '\.\-]{1,148}[\p{L}\p{M}]$",
        ErrorMessage = "El nombre solo puede contener letras, espacios, apóstrofes, guiones y puntos.")]
    public string FullName
    {
        get => _fullName;
        set => _fullName = InputNormalization.CollapseSpaces(value);
    }
}

/// <summary>Datos de entrada para el login por contraseña.</summary>
public class LoginRequest
{
    private string _email = string.Empty;

    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "El correo no tiene un formato válido.")]
    [RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "El correo no tiene un formato válido.")]
    [MaxLength(150)]
    public string Email
    {
        get => _email;
        set => _email = InputNormalization.NormalizeEmail(value);
    }

    [Required(ErrorMessage = "La contraseña es obligatoria.")]
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
