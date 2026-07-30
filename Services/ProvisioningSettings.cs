namespace idempotencia.Services;

/// <summary>
/// Configuración de aprovisionamiento común a TODOS los motores, enlazada desde
/// la sección <c>Provisioning</c> de <c>appsettings.json</c>/secrets/entorno.
///
/// Existe para separar dos direcciones que antes se confundían en una sola
/// clave por motor:
///
/// <list type="bullet">
///   <item>
///     <b>Cómo se conecta el backend al motor</b> → cada
///     <c>Provisioning:{Engine}:AdminConnectionString</c>. En despliegue eso es
///     la red interna de Docker (el nombre del contenedor, p. ej.
///     <c>colmena-postgres</c>): un nombre que solo resuelve dentro de esa red.
///   </item>
///   <item>
///     <b>Cómo se conecta el usuario final a su BD</b> → <see cref="IpVps"/>.
///     Es lo que se le entrega en la respuesta de la API, y tiene que ser una
///     dirección alcanzable desde internet.
///   </item>
/// </list>
///
/// Antes el host reportado salía de <c>Provisioning:{Engine}:Host</c>, que en
/// despliegue terminaba con el nombre del contenedor (inservible para el
/// usuario) y, si la clave faltaba, caía en silencio a <c>localhost</c>.
/// Ver docs/bugs.md.
/// </summary>
public class ProvisioningSettings
{
    public const string SectionName = "Provisioning";

    /// <summary>
    /// Host público del servidor de bases de datos: la IP del VPS (o el dominio
    /// que apunte a él) que se entrega a los usuarios para conectarse a las BDs
    /// aprovisionadas. Es un único valor para los cuatro motores porque todos
    /// corren en la misma máquina; lo que cambia por motor es el puerto
    /// publicado (<c>Provisioning:{Engine}:Port</c>).
    ///
    /// No tiene default a propósito: <c>Program.cs</c> valida al arrancar que
    /// esté configurado y falla de una si no está, en vez de repetir el bug del
    /// <c>localhost</c> silencioso.
    /// </summary>
    public string IpVps { get; set; } = string.Empty;
}
