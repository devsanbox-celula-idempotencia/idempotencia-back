using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Models;
using idempotencia.Services;
using Microsoft.Extensions.Options;

namespace idempotencia.Provisioners;

/// <summary>
/// Implementación de <see cref="IDnsProvider"/> contra la API v4 de Cloudflare.
/// Vive en <c>Provisioners/</c> junto a los provisioners de bases de datos
/// porque cumple el mismo rol arquitectónico: es un adaptador hacia un sistema
/// externo que el catálogo (SQL Server) no puede operar por sí mismo.
///
/// Se registra como <c>HttpClient</c> tipado (ver <c>Program.cs</c>), no
/// instanciando <see cref="HttpClient"/> a mano: así el handler se reutiliza y
/// se recicla solo, evitando tanto el agotamiento de sockets como el problema
/// contrario —un handler eterno que no se entera de un cambio de DNS del
/// proveedor—.
/// </summary>
public class CloudflareDnsProvider : IDnsProvider
{
    private readonly HttpClient _http;
    private readonly DnsSettings _settings;
    private readonly ILogger<CloudflareDnsProvider> _logger;

    /// <summary>
    /// Códigos de error de Cloudflare que significan "ese nombre ya está
    /// tomado". Son dos porque la API responde uno u otro según el tipo de
    /// conflicto (registro idéntico vs. registro incompatible con el mismo
    /// nombre), y para el usuario ambos son la misma situación.
    /// </summary>
    private static readonly int[] RecordAlreadyExistsCodes = { 81053, 81057, 81058 };

    /// <summary>
    /// Códigos que significan "ese registro no existe" (el borrado ya está
    /// aplicado). Deliberadamente NO incluye 7000/7003 ("no route for that
    /// URI"): esos aparecen cuando el ZoneId está mal configurado, y tratarlos
    /// como "ya no existe" convertiría un despliegue roto en un borrado
    /// silencioso y exitoso — el catálogo se limpiaría mientras los registros
    /// reales siguen vivos en la zona.
    /// </summary>
    private static readonly int[] RecordNotFoundCodes = { 81044, 81045 };

    public string Provider => "Cloudflare";
    public string ZoneName => _settings.ZoneName;

    public CloudflareDnsProvider(
        HttpClient http, IOptions<DnsSettings> settings, ILogger<CloudflareDnsProvider> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DnsProvisionResult> CreateAsync(
        string fqdn, string recordType, string content, bool proxied, int ttl,
        CancellationToken ct = default)
    {
        var payload = BuildPayload(fqdn, recordType, content, proxied, ttl);

        using var response = await _http.PostAsJsonAsync(RecordsPath(), payload, ct);
        var envelope = await ReadEnvelopeAsync<CloudflareDnsRecord>(response, ct);

        if (!IsOk(response, envelope))
        {
            // Colisión de nombre → 409, no 500: es una decisión del usuario que
            // se puede corregir eligiendo otra etiqueta. El catálogo ya intenta
            // atajarlo antes (sp_ReserveDnsRecord valida unicidad), pero la zona
            // puede tener registros creados a mano desde el panel de Cloudflare
            // que el catálogo no conoce, así que este caso es real.
            if (HasAnyCode(envelope, RecordAlreadyExistsCodes))
            {
                throw new AppException(
                    $"El subdominio '{fqdn}' ya está en uso. Elige otra etiqueta.",
                    StatusCodes.Status409Conflict);
            }

            throw BuildProviderException(response, envelope, $"crear el registro DNS '{fqdn}'");
        }

        var result = envelope!.Result!;

        _logger.LogInformation(
            "Registro DNS creado en {Provider}: {Fqdn} ({Type}) -> {Content}",
            Provider, result.Name, recordType, content);

        // Se devuelve el nombre que reporta Cloudflare, no el que se envió: la
        // API normaliza (minúsculas, Punycode) y el catálogo debe guardar lo que
        // realmente quedó creado, o el borrado por nombre no lo encontraría.
        return new DnsProvisionResult(result.Id, result.Name);
    }

    public async Task UpdateAsync(
        string providerRecordId, string fqdn, string recordType, string content,
        bool proxied, int ttl, CancellationToken ct = default)
    {
        var payload = BuildPayload(fqdn, recordType, content, proxied, ttl);

        // PUT (reemplazo completo) y no PATCH: el catálogo ya es la fuente de
        // verdad de todos los campos del registro, así que enviar el estado
        // completo hace converger al proveedor aunque hubiera divergido.
        using var response = await _http.PutAsJsonAsync(RecordPath(providerRecordId), payload, ct);
        var envelope = await ReadEnvelopeAsync<CloudflareDnsRecord>(response, ct);

        if (!IsOk(response, envelope))
        {
            if (IsNotFound(response, envelope))
            {
                throw new NotFoundException(
                    "El registro DNS ya no existe en el proveedor. Elimínalo y vuelve a crearlo.");
            }

            throw BuildProviderException(response, envelope, $"actualizar el registro DNS '{fqdn}'");
        }

        _logger.LogInformation(
            "Registro DNS actualizado en {Provider}: {Fqdn} -> {Content}", Provider, fqdn, content);
    }

    public async Task DeleteAsync(string providerRecordId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(RecordPath(providerRecordId), ct);
        var envelope = await ReadEnvelopeAsync<CloudflareDnsRecord>(response, ct);

        // Idempotencia deliberada: si el registro ya no está, el estado final
        // buscado ("ese nombre no resuelve") ya se cumple. Lanzar acá dejaría al
        // usuario sin poder limpiar una fila del catálogo cuyo registro alguien
        // borró a mano desde el panel de Cloudflare.
        if (IsNotFound(response, envelope))
        {
            _logger.LogInformation(
                "El registro DNS {RecordId} ya no existía en {Provider}; se trata como borrado.",
                providerRecordId, Provider);
            return;
        }

        if (!IsOk(response, envelope))
            throw BuildProviderException(response, envelope, $"eliminar el registro DNS {providerRecordId}");

        _logger.LogInformation("Registro DNS {RecordId} eliminado en {Provider}.", providerRecordId, Provider);
    }

    public async Task<string?> FindRecordIdAsync(string fqdn, CancellationToken ct = default)
    {
        // name.exact es el filtro documentado para coincidencia exacta; el
        // parámetro suelto "name" hace coincidencia parcial en la API actual y
        // podría devolver un registro vecino (p. ej. "web" al buscar "web2").
        var path = $"{RecordsPath()}?name.exact={Uri.EscapeDataString(fqdn)}&per_page=1";

        using var response = await _http.GetAsync(path, ct);
        var envelope = await ReadEnvelopeAsync<List<CloudflareDnsRecord>>(response, ct);

        if (!IsOk(response, envelope))
        {
            // Es una operación auxiliar de reconciliación, no el objetivo del
            // request: si falla, se registra y se devuelve null para que el
            // llamador siga con su propio manejo, en vez de tumbar la operación
            // principal por no haber podido hacer una búsqueda de cortesía.
            _logger.LogWarning(
                "No se pudo consultar el registro DNS '{Fqdn}' en {Provider} ({Status}).",
                fqdn, Provider, (int)response.StatusCode);
            return null;
        }

        return envelope!.Result?.FirstOrDefault()?.Id;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string RecordsPath() => $"zones/{_settings.ZoneId}/dns_records";

    private string RecordPath(string recordId) => $"{RecordsPath()}/{recordId}";

    private static CloudflareDnsRecordPayload BuildPayload(
        string fqdn, string recordType, string content, bool proxied, int ttl) =>
        new(
            Type: recordType,
            Name: fqdn,
            Content: content,
            // Cloudflare rechaza un registro proxeado con TTL explícito: al
            // pasar por el proxy el TTL lo gobierna el borde, no el registro.
            // Se normaliza acá y no en el servicio para que la regla viva junto
            // al proveedor que la impone.
            Ttl: proxied ? 1 : ttl,
            Proxied: proxied,
            Comment: "Creado por la plataforma Colmena (idempotencia-back).");

    private static async Task<CloudflareEnvelope<T>?> ReadEnvelopeAsync<T>(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<CloudflareEnvelope<T>>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Un cuerpo que no es el JSON esperado no debe convertirse en una
            // excepción opaca: pasa con el HTML de una página de error del borde
            // (JsonException), con un Content-Type que no es JSON
            // (NotSupportedException) y con una respuesta sin cuerpo. Se devuelve
            // null y el llamador arma el mensaje a partir del código HTTP, que en
            // ese caso es la única información confiable.
            return null;
        }
    }

    private static bool IsOk<T>(HttpResponseMessage response, CloudflareEnvelope<T>? envelope) =>
        response.IsSuccessStatusCode && envelope is { Success: true, Result: not null };

    private static bool HasAnyCode<T>(CloudflareEnvelope<T>? envelope, int[] codes) =>
        envelope?.Errors?.Any(e => codes.Contains(e.Code)) == true;

    /// <summary>
    /// "El registro no existe" llega de dos formas distintas según el endpoint:
    /// como 404 HTTP, o como 200 con <c>success:false</c> y un código de error
    /// en el cuerpo. Se contemplan ambas.
    /// </summary>
    private static bool IsNotFound<T>(HttpResponseMessage response, CloudflareEnvelope<T>? envelope) =>
        response.StatusCode == HttpStatusCode.NotFound || HasAnyCode(envelope, RecordNotFoundCodes);

    /// <summary>
    /// Traduce un fallo del proveedor a la excepción que verá el cliente. Los
    /// errores de credenciales/permisos (401/403) se devuelven como 500 a
    /// propósito: son un problema de configuración de la plataforma, no algo
    /// que el usuario pueda corregir, y devolverle un 403 le haría creer que le
    /// falta un permiso propio. El detalle real queda en el log.
    /// </summary>
    private AppException BuildProviderException<T>(
        HttpResponseMessage response, CloudflareEnvelope<T>? envelope, string action)
    {
        var detail = envelope?.Errors is { Count: > 0 }
            ? string.Join("; ", envelope.Errors.Select(e => $"[{e.Code}] {e.Message}"))
            : $"HTTP {(int)response.StatusCode}";

        _logger.LogError("Fallo al {Action} en {Provider}: {Detail}", action, Provider, detail);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new AppException(
                "El servicio de DNS no está disponible en este momento.",
                StatusCodes.Status500InternalServerError);
        }

        return new AppException(
            $"No se pudo {action}. Inténtalo de nuevo más tarde.",
            StatusCodes.Status502BadGateway);
    }

    // -----------------------------------------------------------------------
    // Contrato JSON de la API de Cloudflare (solo los campos que se usan)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sobre común de todas las respuestas de la API v4: el código HTTP no
    /// alcanza para saber si salió bien — Cloudflare puede responder 200 con
    /// <c>success:false</c> —, así que siempre hay que mirar el cuerpo.
    /// </summary>
    private sealed record CloudflareEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("errors")] List<CloudflareError>? Errors,
        [property: JsonPropertyName("result")] T? Result);

    private sealed record CloudflareError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record CloudflareDnsRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record CloudflareDnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("proxied")] bool Proxied,
        [property: JsonPropertyName("comment")] string Comment);
}
