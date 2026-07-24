using System.Text.RegularExpressions;

namespace idempotencia.DTOs;

/// <summary>
/// Normalización y validación de texto de entrada compartida por los DTOs de
/// request. La normalización se aplica en los setters de las propiedades
/// (antes de que corra la validación), así:
///   - Un correo con espacios de más (copy/paste) o mayúsculas inconsistentes
///     ("Ana@Uni.edu ") no genera un 400 confuso ni crea una cuenta "distinta"
///     de "ana@uni.edu" por una diferencia puramente cosmética.
///   - Un nombre con espacios dobles/de más queda prolijo antes de guardarse.
///
/// La validación de formato de email vive ACÁ (un solo método, un solo
/// regex) a propósito — antes estaba repartida entre el atributo
/// <c>[EmailAddress]</c> de .NET y un <c>[RegularExpression]</c> propio
/// aplicados juntos sobre la misma propiedad. Ambos corren de forma
/// independiente y ambos deben pasar; esa combinación causó un bug real
/// (`POST /auth/register` rechazaba con 400 un correo de exactamente 150
/// caracteres, el máximo documentado, porque no se podía garantizar que las
/// dos validaciones coincidieran exactamente en el límite). Ahora hay un solo
/// punto de verdad, se invoca desde <c>IValidatableObject.Validate</c> en
/// cada DTO (ver <see cref="idempotencia.DTOs.RegisterRequest"/>/
/// <see cref="idempotencia.DTOs.LoginRequest"/>), y es trivial de testear en
/// aislamiento.
///
/// No reemplaza la protección contra SQL injection (parámetros tipados +
/// escape de identificadores en los provisioners) — es una capa adicional de
/// calidad de dato, no la única defensa.
/// </summary>
internal static class InputNormalization
{
    // Simple y explícito a propósito: "algo" + "@" + "algo" + "." + "algo",
    // sin "@" ni espacios en ninguna de las tres partes. Cubre el 99% de los
    // correos reales sin la complejidad (ni los casos borde) del RFC 5322
    // completo. Se prueba contra cadenas de cualquier longitud sin
    // backtracking catastrófico (cada cuantificador es de un solo tipo de
    // carácter, sin ambigüedad).
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public const int MaxEmailLength = 150;

    public static string TrimOrEmpty(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>Trim + minúsculas, para que el mismo correo siempre se compare/guarde igual.</summary>
    public static string NormalizeEmail(string? value) => TrimOrEmpty(value).ToLowerInvariant();

    /// <summary>Trim + colapsa espacios internos repetidos a uno solo ("Ana   Pérez" → "Ana Pérez").</summary>
    public static string CollapseSpaces(string? value) =>
        Regex.Replace(TrimOrEmpty(value), @"\s+", " ");

    /// <summary>
    /// Único punto de verdad para "¿este correo tiene formato válido?". No
    /// valida longitud — eso se hace aparte con <see cref="IsWithinMaxEmailLength"/>
    /// para que cada regla tenga su propio mensaje de error claro.
    /// </summary>
    public static bool IsValidEmailFormat(string email) => EmailPattern.IsMatch(email);

    /// <summary>
    /// Verificado explícitamente con <c>&lt;=</c> (inclusivo): un correo de
    /// exactamente <see cref="MaxEmailLength"/> caracteres es válido.
    /// </summary>
    public static bool IsWithinMaxEmailLength(string email) => email.Length <= MaxEmailLength;
}
