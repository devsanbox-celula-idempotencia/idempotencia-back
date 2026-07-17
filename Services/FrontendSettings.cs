namespace idempotencia.Services;

/// <summary>Opciones del frontend, enlazadas desde la sección "Frontend".</summary>
public class FrontendSettings
{
    public const string SectionName = "Frontend";

    public string BaseUrl { get; set; } = "http://localhost:5555";
}
