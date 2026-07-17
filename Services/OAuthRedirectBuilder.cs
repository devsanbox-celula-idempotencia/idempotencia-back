using idempotencia.DTOs;
using idempotencia.Interfaces;
using Microsoft.Extensions.Options;

namespace idempotencia.Services;

/// <summary>
/// Implementación de <see cref="IOAuthRedirectBuilder"/>. Arma la URL
/// <c>{BaseUrl}/oauth/callback</c> con los datos codificados como query string.
/// </summary>
public class OAuthRedirectBuilder : IOAuthRedirectBuilder
{
    private readonly FrontendSettings _settings;

    public OAuthRedirectBuilder(IOptions<FrontendSettings> settings) => _settings = settings.Value;

    public string BuildSuccess(AuthResponse response)
    {
        var query = new QueryString()
            .Add("token", response.Token)
            .Add("expiresAt", response.ExpiresAt.ToString("o"))
            .Add("userId", response.UserId.ToString())
            .Add("email", response.Email)
            .Add("fullName", response.FullName)
            .Add("role", response.Role);

        return CallbackUrl + query.ToUriComponent();
    }

    public string BuildError(string message)
    {
        var query = new QueryString().Add("error", message);
        return CallbackUrl + query.ToUriComponent();
    }

    private string CallbackUrl => $"{_settings.BaseUrl.TrimEnd('/')}/oauth/callback";
}
