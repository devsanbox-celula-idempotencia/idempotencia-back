namespace idempotencia.Services;

/// <summary>
/// Opciones de configuración del JWT, enlazadas desde la sección "Jwt" de
/// appsettings/secrets. Nunca hardcodear estos valores.
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Clave simétrica de firma (viene de secrets/entorno).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Minutos de validez del token.</summary>
    public int ExpirationMinutes { get; set; } = 60;
}
