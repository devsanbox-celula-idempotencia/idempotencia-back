using System.Security.Claims;
using idempotencia.DTOs;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace idempotencia.Controllers;

/// <summary>
/// Autenticación: registro/login por contraseña y flujos OAuth de Google y
/// GitHub. Es un mediador; delega en <see cref="IAuthService"/> y no contiene
/// reglas de negocio.
/// </summary>
[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IOAuthRedirectBuilder _redirect;

    public AuthController(IAuthService auth, IOAuthRedirectBuilder redirect)
    {
        _auth = auth;
        _redirect = redirect;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken ct)
    {
        return Ok(await _auth.RegisterAsync(request, ct));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        return Ok(await _auth.LoginAsync(request, ct));
    }

    [HttpGet("google/login")]
    [AllowAnonymous]
    public IActionResult GoogleLogin() => Challenge(
        new AuthenticationProperties { RedirectUri = Url.Action(nameof(GoogleCallback)) }, "Google");

    [HttpGet("google/callback")]
    [AllowAnonymous]
    public Task<IActionResult> GoogleCallback(CancellationToken ct) => ExternalCallback("Google", ct);

    [HttpGet("github/login")]
    [AllowAnonymous]
    public IActionResult GitHubLogin() => Challenge(
        new AuthenticationProperties { RedirectUri = Url.Action(nameof(GitHubCallback)) }, "GitHub");

    [HttpGet("github/callback")]
    [AllowAnonymous]
    public Task<IActionResult> GitHubCallback(CancellationToken ct) => ExternalCallback("GitHub", ct);

    // Resuelve la identidad externa y redirige al frontend con la sesión o el error.
    private async Task<IActionResult> ExternalCallback(string provider, CancellationToken ct)
    {
        try
        {
            var response = await ResolveExternalLoginAsync(provider, ct);
            return Redirect(_redirect.BuildSuccess(response));
        }
        catch (Exception ex)
        {
            var (_, message) = ApiExceptionMapper.Map(ex);
            return Redirect(_redirect.BuildError(message));
        }
    }

    // Lee la cookie temporal "External", extrae los datos del proveedor y delega
    // en el servicio, que invoca sp_UpsertExternalLogin.
    private async Task<AuthResponse> ResolveExternalLoginAsync(string provider, CancellationToken ct)
    {
        var result = await HttpContext.AuthenticateAsync("External");
        if (!result.Succeeded || result.Principal is null)
            throw new AuthException("No se pudo completar la autenticación externa.");

        var principal = result.Principal;
        var providerUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();

        // Aunque Google/GitHub son proveedores confiables, se normaliza igual
        // que en los DTOs de registro/login (trim + minúsculas en el correo)
        // para que un mismo usuario no termine con variantes de mayúsculas
        // distintas según cómo haya iniciado sesión, y para no persistir
        // espacios en blanco si el proveedor los incluyera.
        var email = InputNormalization.NormalizeEmail(principal.FindFirstValue(ClaimTypes.Email));
        var fullName = InputNormalization.CollapseSpaces(
            principal.FindFirstValue(ClaimTypes.Name) ?? email);

        if (string.IsNullOrEmpty(providerUserId) || string.IsNullOrEmpty(email))
            throw new AuthException("El proveedor externo no entregó los datos mínimos (id/email).");

        await HttpContext.SignOutAsync("External");

        return await _auth.ExternalLoginAsync(provider, providerUserId, email, fullName, ct);
    }
}
