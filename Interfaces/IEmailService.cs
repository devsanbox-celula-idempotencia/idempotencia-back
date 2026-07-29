namespace idempotencia.Interfaces;

/// <summary>
/// Envío de correo saliente. Abstrae el proveedor (hoy SMTP de Gmail/Workspace,
/// ver <see cref="idempotencia.Services.SmtpEmailService"/>) para poder
/// cambiarlo después (SendGrid, SES, etc.) sin tocar quien lo consume.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Envía un correo. Las implementaciones NO deben lanzar si el envío falla
    /// por un problema transitorio de red del proveedor SMTP sin que el
    /// llamador pueda decidir qué hacer — ver el manejo específico en
    /// <c>DatabaseProvisioningService.ResetPasswordAsync</c>, que sí propaga la
    /// excepción a propósito (si el correo no llega, el usuario nunca se
    /// entera de su nueva contraseña, así que ahí NO se debe fallar en
    /// silencio).
    /// </summary>
    Task SendAsync(string toEmail, string toName, string subject, string bodyHtml, CancellationToken ct = default);
}
