using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace idempotencia.Controllers;

/// <summary>
/// Endpoints de autenticación: registro/login por contraseña y flujos OAuth
/// (challenge/callback) de Google y GitHub. El controller es un mediador: no
/// contiene reglas de negocio, solo delega en <see cref="IAuthService"/>.
/// </summary>
[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    /// <summary>Registro por contraseña. Devuelve un JWT.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken ct)
    {
        var response = await _auth.RegisterAsync(request, ct);
        return Ok(response);
    }

    /// <summary>Login por contraseña. Devuelve un JWT.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        var response = await _auth.LoginAsync(request, ct);
        return Ok(response);
    }

    // ---------------------------------------------------------------------
    // OAuth: Google
    // ---------------------------------------------------------------------

    /// <summary>
    /// Inicia el flujo OAuth de Google. Redirige al proveedor y, tras el
    /// consentimiento, vuelve al callback.
    /// </summary>
    [HttpGet("google/login")]
    [AllowAnonymous]
    public IActionResult GoogleLogin()
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(GoogleCallback))
        };
        return Challenge(props, "Google");
    }

    /// <summary>Callback de Google: resuelve la identidad y firma el JWT.</summary>
    [HttpGet("google/callback")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> GoogleCallback(CancellationToken ct)
    {
        var response = await HandleExternalCallbackAsync("Google", ct);
        return Ok(response);
    }

    // ---------------------------------------------------------------------
    // OAuth: GitHub
    // ---------------------------------------------------------------------

    /// <summary>Inicia el flujo OAuth de GitHub.</summary>
    [HttpGet("github/login")]
    [AllowAnonymous]
    public IActionResult GitHubLogin()
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(GitHubCallback))
        };
        return Challenge(props, "GitHub");
    }

    /// <summary>Callback de GitHub: resuelve la identidad y firma el JWT.</summary>
    [HttpGet("github/callback")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> GitHubCallback(CancellationToken ct)
    {
        var response = await HandleExternalCallbackAsync("GitHub", ct);
        return Ok(response);
    }

    // ---------------------------------------------------------------------
    // Lógica común de callback OAuth
    // ---------------------------------------------------------------------

    /// <summary>
    /// Lee el resultado de la autenticación externa (cookie temporal "External"),
    /// extrae Provider + ProviderUserId + Email + FullName y delega en el
    /// servicio de autenticación, que invoca <c>sp_UpsertExternalLogin</c>.
    /// </summary>
    private async Task<AuthResponse> HandleExternalCallbackAsync(string provider, CancellationToken ct)
    {
        // El SignInScheme de los handlers OAuth es la cookie "External".
        var result = await HttpContext.AuthenticateAsync("External");

        if (!result.Succeeded || result.Principal is null)
            throw new AuthException("No se pudo completar la autenticación externa.");

        var principal = result.Principal;

        var providerUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = principal.FindFirstValue(ClaimTypes.Email);
        var fullName = principal.FindFirstValue(ClaimTypes.Name) ?? email ?? string.Empty;

        // GitHub puede devolver el email como privado; el handler intenta
        // resolverlo con el scope "user:email" (ver Program.cs). Si aún así
        // no llega, no podemos vincular la cuenta de forma fiable.
        if (string.IsNullOrEmpty(providerUserId) || string.IsNullOrEmpty(email))
            throw new AuthException(
                "El proveedor externo no entregó los datos mínimos (id/email).");

        // Se limpia la cookie temporal una vez extraídos los datos.
        await HttpContext.SignOutAsync("External");

        return await _auth.ExternalLoginAsync(provider, providerUserId, email, fullName, ct);
    }
}
