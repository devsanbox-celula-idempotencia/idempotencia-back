using idempotencia.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace idempotencia.Services;

/// <summary>
/// Implementación de <see cref="IEmailService"/> vía SMTP (pensada para Gmail/
/// Google Workspace, pero sirve para cualquier proveedor SMTP estándar). Usa
/// MailKit en vez de <c>System.Net.Mail.SmtpClient</c> (obsoleto/desaconsejado
/// por Microsoft para código nuevo).
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<EmailSettings> settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        string toEmail, string toName, string subject, string bodyHtml, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = bodyHtml }.ToMessageBody();

        using var client = new SmtpClient();

        try
        {
            // 587 = STARTTLS (SecureSocketOptions.StartTls); 465 = SSL implícito.
            var secureOption = _settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_settings.Host, _settings.Port, secureOption, ct);
            await client.AuthenticateAsync(_settings.Username, _settings.Password, ct);
            await client.SendAsync(message, ct);
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, ct);
        }
    }
}
