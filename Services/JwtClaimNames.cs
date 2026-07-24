namespace idempotencia.Services;

/// <summary>
/// Nombres de los claims personalizados que emite y consume el backend en su
/// propio JWT. Centralizarlos evita que el nombre quede escrito como string
/// literal en varios lugares (emisión en <see cref="JwtTokenService"/>, lectura
/// en los controllers y en la partición del rate limiter): si el nombre cambia,
/// se cambia acá una sola vez y el compilador obliga a que todo siga en sync.
/// </summary>
public static class JwtClaimNames
{
    /// <summary>Identificador numérico del usuario (clave primaria en el catálogo).</summary>
    public const string UserId = "UserId";
}
