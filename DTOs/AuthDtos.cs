using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>Datos de entrada para el registro por contraseña.</summary>
public class RegisterRequest : IValidatableObject
{
    private string _email = string.Empty;
    private string _fullName = string.Empty;

    // Sin [EmailAddress]/[RegularExpression]/[MaxLength] apilados a propósito
    // — esa combinación causó un bug real (400 en un correo de exactamente
    // 150 caracteres, el máximo documentado: dos validadores independientes
    // sobre el mismo campo no garantizan coincidir en el límite exacto). El
    // formato y la longitud se validan en un solo lugar, en Validate() más
    // abajo, usando InputNormalization como único punto de verdad. El valor
    // ya llega trimeado/en minúsculas por el setter, así que un correo con
    // mayúsculas o espacios de más no genera un 400 innecesario ni crea una
    // cuenta "distinta" por una diferencia cosmética.
    public string Email
    {
        get => _email;
        set => _email = InputNormalization.NormalizeEmail(value);
    }

    // A propósito NO se trimea/normaliza: un espacio en la contraseña puede
    // ser intencional y parte de la clave real del usuario.
    // Límite máximo bajado de 100 a 12 (requisito de negocio confirmado,
    // ver docs/bugs.md ítem 21) — ¡ojo!, deja un rango angosto (8-12), no
    // aumentar el mínimo sin volver a confirmar el rango con el equipo.
    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [MinLength(8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
    [MaxLength(12, ErrorMessage = "La contraseña no puede superar los 12 caracteres.")]
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        EmailValidation.Validate(Email, nameof(Email));
}

/// <summary>Datos de entrada para el login por contraseña.</summary>
public class LoginRequest : IValidatableObject
{
    private string _email = string.Empty;

    public string Email
    {
        get => _email;
        set => _email = InputNormalization.NormalizeEmail(value);
    }

    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    public string Password { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        EmailValidation.Validate(Email, nameof(Email));
}

/// <summary>
/// Validación de <c>Email</c> compartida por <see cref="RegisterRequest"/> y
/// <see cref="LoginRequest"/> — un solo lugar, tres reglas explícitas
/// (obligatorio, longitud, formato), cada una con su propio mensaje. Ver el
/// comentario en <see cref="InputNormalization"/> sobre por qué esto ya no
/// vive en atributos apilados.
/// </summary>
internal static class EmailValidation
{
    public static IEnumerable<ValidationResult> Validate(string email, string memberName)
    {
        if (string.IsNullOrEmpty(email))
        {
            yield return new ValidationResult("El correo es obligatorio.", new[] { memberName });
            yield break; // sin valor, no tiene sentido evaluar longitud/formato
        }

        if (!InputNormalization.IsWithinMaxEmailLength(email))
        {
            yield return new ValidationResult(
                $"El correo no puede superar los {InputNormalization.MaxEmailLength} caracteres.",
                new[] { memberName });
        }

        if (!InputNormalization.IsValidEmailFormat(email))
        {
            yield return new ValidationResult("El correo no tiene un formato válido.", new[] { memberName });
        }
    }
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
