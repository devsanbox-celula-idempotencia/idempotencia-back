namespace idempotencia.Services;

/// <summary>
/// Configuración del servicio de correo saliente (SMTP). Se enlaza desde la
/// sección <c>Email</c> de <c>appsettings.json</c>. Igual que <see cref="JwtSettings"/>,
/// los valores reales (usuario/contraseña de aplicación) NO deben quedar en
/// texto plano en el repo — usar User Secrets/variables de entorno (ver
/// docs/bugs.md ítem 3).
/// </summary>
public class EmailSettings
{
    public const string SectionName = "Email";

    /// <summary>Host SMTP (ej. "smtp.gmail.com" para Gmail/Workspace).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Puerto SMTP (587 = STARTTLS, 465 = SSL implícito).</summary>
    public int Port { get; set; } = 587;

    /// <summary>Usuario de la cuenta que envía (la dirección completa de Gmail/Workspace).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Contraseña de aplicación (App Password) de la cuenta — NO la contraseña
    /// normal de la cuenta de Google. Se genera en la configuración de
    /// seguridad de la cuenta de Google (requiere verificación en 2 pasos
    /// activada).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Dirección que aparece como remitente (normalmente igual a <see cref="Username"/>).</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Nombre para mostrar del remitente (ej. "Colmena").</summary>
    public string FromName { get; set; } = "Colmena";
}
