namespace idempotencia.DTOs;

/// <summary>
/// Normalización de texto de entrada compartida por los DTOs de request.
/// Se aplica en los setters de las propiedades (antes de que corra la
/// validación de DataAnnotations en <c>[ApiController]</c>), así:
///   - Un correo con espacios de más (copy/paste) o mayúsculas inconsistentes
///     ("Ana@Uni.edu ") no genera un 400 confuso ni crea una cuenta "distinta"
///     de "ana@uni.edu" por una diferencia puramente cosmética.
///   - Un nombre con espacios dobles/de más queda prolijo antes de guardarse.
/// No reemplaza la validación de formato (<c>[RegularExpression]</c> en cada
/// DTO) ni la protección contra SQL injection (parámetros tipados +
/// escape de identificadores en los provisioners) — es una capa adicional de
/// calidad de dato, no la única defensa.
/// </summary>
internal static class InputNormalization
{
    public static string TrimOrEmpty(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>Trim + minúsculas, para que el mismo correo siempre se compare/guarde igual.</summary>
    public static string NormalizeEmail(string? value) => TrimOrEmpty(value).ToLowerInvariant();

    /// <summary>Trim + colapsa espacios internos repetidos a uno solo ("Ana   Pérez" → "Ana Pérez").</summary>
    public static string CollapseSpaces(string? value) =>
        System.Text.RegularExpressions.Regex.Replace(TrimOrEmpty(value), @"\s+", " ");
}
