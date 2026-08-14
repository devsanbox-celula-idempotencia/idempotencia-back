using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.OpenApi;
using idempotencia.Provisioners;
using idempotencia.Repository;
using idempotencia.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuración (enlace de opciones)
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.SectionName));

var jwtSettings = builder.Configuration
    .GetSection(JwtSettings.SectionName)
    .Get<JwtSettings>() ?? new JwtSettings();

builder.Services.Configure<FrontendSettings>(
    builder.Configuration.GetSection(FrontendSettings.SectionName));

builder.Services.Configure<EmailSettings>(
    builder.Configuration.GetSection(EmailSettings.SectionName));

builder.Services.Configure<SizeMonitorSettings>(
    builder.Configuration.GetSection(SizeMonitorSettings.SectionName));

// Host público (IP del VPS o dominio) que se entrega a los usuarios para
// conectarse a sus BDs. Es distinto del host con el que el backend habla con
// cada motor: ese vive en Provisioning:{Engine}:AdminConnectionString y en
// despliegue es el nombre del contenedor en la red interna de Docker, que no
// resuelve desde afuera. Se valida acá (mismo criterio que Cors:AllowedOrigins)
// porque un despliegue sin esta clave entrega datos de conexión inservibles SIN
// fallar en ningún momento: el error recién aparecería en el cliente, al no
// poder conectarse.
builder.Services.Configure<ProvisioningSettings>(
    builder.Configuration.GetSection(ProvisioningSettings.SectionName));

if (string.IsNullOrWhiteSpace(builder.Configuration["Provisioning:IpVps"]))
{
    throw new InvalidOperationException(
        "Falta configurar Provisioning:IpVps en appsettings.json: la IP pública del " +
        "VPS (o el dominio que apunte a él) que se entrega a los usuarios para " +
        "conectarse a sus bases de datos. En local usar \"localhost\".");
}

// Separación catálogo / aprovisionamiento de SQL Server. Son dos servidores
// distintos desde 2026-08-14: el catálogo (tablas y SPs de control) sigue en
// ConnectionStrings:Colmena, y las bases de los estudiantes se crean en la
// instancia del proveedor (Provisioning:SqlServer:AdminConnectionString).
//
// Se valida acá y no en el provisioner porque un fallo de esta configuración no
// se nota: sin la clave, la versión anterior caía en silencio a la cadena del
// catálogo y creaba las bases de los estudiantes dentro del servidor de control.
// Mejor no arrancar.
var sqlServerAdmin = builder.Configuration["Provisioning:SqlServer:AdminConnectionString"];

if (string.IsNullOrWhiteSpace(sqlServerAdmin))
{
    throw new InvalidOperationException(
        "Falta Provisioning:SqlServer:AdminConnectionString: la cadena del servidor donde se " +
        "crean las bases de los estudiantes. Ya no se reusa ConnectionStrings:Colmena como " +
        "respaldo — son dos servidores distintos y mezclarlos es justo lo que se quiso evitar.");
}

// Coincidencia exacta con la del catálogo: no se falla (un ambiente local puede
// tener todo junto a propósito) pero se avisa fuerte, porque en despliegue casi
// siempre significa que alguien copió la clave equivocada.
if (string.Equals(sqlServerAdmin, builder.Configuration.GetConnectionString("Colmena"),
        StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(
        "[ADVERTENCIA] Provisioning:SqlServer:AdminConnectionString es idéntica a " +
        "ConnectionStrings:Colmena: las bases de los estudiantes se van a crear en el mismo " +
        "servidor que el catálogo. Correcto solo en un ambiente local; en despliegue revisa " +
        "la configuración.");
}

// MySQL delegado en la API de la célula socia. Mismo patrón que el bloque de
// Mongo de abajo: se enlaza siempre y el interruptor Enabled decide cuál de los
// dos provisioners se registra.
builder.Services.Configure<RemoteMySqlSettings>(
    builder.Configuration.GetSection(RemoteMySqlSettings.SectionName));

var remoteMySql = builder.Configuration
    .GetSection(RemoteMySqlSettings.SectionName)
    .Get<RemoteMySqlSettings>() ?? new RemoteMySqlSettings();

if (remoteMySql.Enabled && string.IsNullOrWhiteSpace(remoteMySql.ApiKey))
{
    throw new InvalidOperationException(
        "Provisioning:MySql:Remote:Enabled está en true pero falta " +
        "Provisioning:MySql:Remote:ApiKey. Configúrala (preferiblemente por " +
        "variable de entorno Provisioning__MySql__Remote__ApiKey) o pon Enabled " +
        "en false para volver al provisioner local de MySQL.");
}

// MongoDB delegado en la API externa del equipo. Se enlaza siempre; el
// interruptor Enabled decide cuál de los dos provisioners de Mongo se registra
// más abajo.
builder.Services.Configure<RemoteMongoSettings>(
    builder.Configuration.GetSection(RemoteMongoSettings.SectionName));

var remoteMongo = builder.Configuration
    .GetSection(RemoteMongoSettings.SectionName)
    .Get<RemoteMongoSettings>() ?? new RemoteMongoSettings();

// Mismo criterio que Provisioning:IpVps y Dns:ApiToken: se falla al arrancar y
// no en la primera petición del usuario. Una API key vacía acá no produce un
// error obvio —produce un 502 en mitad de una creación, con la reserva del
// catálogo ya hecha y revertida— así que es exactamente el tipo de fallo que
// conviene adelantar al despliegue.
if (remoteMongo.Enabled && string.IsNullOrWhiteSpace(remoteMongo.ApiKey))
{
    throw new InvalidOperationException(
        "Provisioning:Mongo:Remote:Enabled está en true pero falta " +
        "Provisioning:Mongo:Remote:ApiKey. Configúrala (preferiblemente por " +
        "variable de entorno Provisioning__Mongo__Remote__ApiKey) o pon Enabled " +
        "en false para volver al provisioner local de MongoDB.");
}

// ---------------------------------------------------------------------------
// DNS (Cloudflare). Mismo criterio de validación al arrancar que Provisioning:
// IpVps y Cors:AllowedOrigins — sin estas claves el servicio de subdominios no
// puede funcionar, y el error no aparecería hasta el primer POST /dns de un
// usuario real. Es preferible que el proceso no levante.
//
// El token es un SECRETO: en despliegue debe venir por variable de entorno
// (Dns__ApiToken) o user-secrets, no del appsettings.json versionado.
// ---------------------------------------------------------------------------
builder.Services.Configure<DnsSettings>(
    builder.Configuration.GetSection(DnsSettings.SectionName));

foreach (var requiredDnsKey in new[] { "Dns:ZoneId", "Dns:ApiToken", "Dns:ZoneName" })
{
    if (string.IsNullOrWhiteSpace(builder.Configuration[requiredDnsKey]))
    {
        throw new InvalidOperationException(
            $"Falta configurar {requiredDnsKey}. La sección Dns necesita el ZoneId de la " +
            "zona en Cloudflare, un token de API con permiso Zone.DNS:Edit sobre esa zona, " +
            "y el nombre del dominio (por ejemplo \"coderhivex.com\"). En despliegue, " +
            "pasar el token por la variable de entorno Dns__ApiToken.");
    }
}


builder.Services.AddDbContext<ColmenaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Colmena")));


builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<IStatisticsRepository, StatisticsRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IOAuthRedirectBuilder, OAuthRedirectBuilder>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<IDnsRepository, DnsRepository>();
builder.Services.AddScoped<IDnsProvisioningService, DnsProvisioningService>();

// Proveedor de DNS como HttpClient tipado: el handler subyacente se comparte y
// se recicla solo, lo que evita a la vez el agotamiento de sockets (un
// HttpClient nuevo por request) y el handler eterno que no se entera de un
// cambio de DNS del proveedor. Las cabeceras de autenticación se configuran acá,
// una sola vez, en vez de en cada llamada.
//
// A diferencia de los provisioners de bases de datos NO hay factory: los motores
// son cuatro y conviven (el usuario elige uno por BD), mientras que el proveedor
// de DNS es uno solo por despliegue. La interfaz sí existe para que agregar
// Route53 mañana no obligue a tocar el servicio orquestador.
builder.Services.AddHttpClient<IDnsProvider, CloudflareDnsProvider>((sp, client) =>
{
    var dns = sp.GetRequiredService<IOptions<DnsSettings>>().Value;

    // BaseAddress DEBE terminar en "/": si no, Uri descarta el último segmento
    // al combinar con la ruta relativa y todas las llamadas irían a /client/.
    var baseUrl = dns.ApiBaseUrl.EndsWith('/') ? dns.ApiBaseUrl : dns.ApiBaseUrl + "/";

    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", dns.ApiToken);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));

    // Timeout corto: es una dependencia externa dentro del ciclo de un request
    // del usuario. Fallar rápido y revertir la reserva es mejor que dejar la
    // petición colgada hasta el timeout por defecto de 100 segundos.
    client.Timeout = TimeSpan.FromSeconds(dns.RequestTimeoutSeconds);
});

// Aprovisionamiento multi-motor: servicio orquestador + factory + un provisioner
// por motor (patrón Strategy). Se registran todos como IDatabaseProvisioner y el
// factory elige el correcto según el motor pedido.
builder.Services.AddScoped<IDatabaseProvisioningService, DatabaseProvisioningService>();
builder.Services.AddScoped<IDatabaseProvisionerFactory, DatabaseProvisionerFactory>();
builder.Services.AddScoped<IDatabaseProvisioner, SqlServerProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, PostgresProvisioner>();

// El motor "MySql" tiene DOS implementaciones y se registra exactamente una,
// por el mismo motivo que Mongo (el factory resuelve por Engine, así que
// registrar ambas dejaría la elección al orden de la colección de DI).
//
// Cuidado adicional al revertir: MySQL es el motor que se aprovisiona solo en
// el primer login por OAuth, así que este interruptor decide dónde nacen las
// bases de todos los usuarios nuevos.
if (remoteMySql.Enabled)
{
    // OJO con el tipo genérico: va la clase CONCRETA, no IDatabaseProvisioner.
    // AddHttpClient<TClient, TImpl> nombra el cliente lógico según TClient, así
    // que registrar los dos provisioners remotos como
    // AddHttpClient<IDatabaseProvisioner, ...> les daba a ambos el mismo nombre
    // ("IDatabaseProvisioner") y la última configuración pisaba a la anterior:
    // el provisioner de MySQL terminaba con el BaseAddress y la cabecera de
    // Mongo, y le pedía /partners/databases a mongo.szapatar.dev (404). Con la
    // clase concreta cada uno tiene su propio nombre —y sus propios logs de
    // HttpClient— y la línea de abajo lo expone como IDatabaseProvisioner para
    // que el factory lo siga encontrando.
    builder.Services.AddHttpClient<RemoteMySqlProvisioner>((sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<RemoteMySqlSettings>>().Value;

        // BaseAddress DEBE terminar en "/": si no, Uri descarta el último
        // segmento al combinar con la ruta relativa.
        var baseUrl = settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/";

        client.BaseAddress = new Uri(baseUrl);

        // Bearer, no X-API-Key: es el esquema que exige la célula socia. No
        // tiene nada que ver con el JWT que este backend le emite a SUS
        // usuarios — son dos credenciales en direcciones opuestas que solo
        // comparten el nombre del header.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
    });

    builder.Services.AddScoped<IDatabaseProvisioner>(
        sp => sp.GetRequiredService<RemoteMySqlProvisioner>());
}
else
{
    builder.Services.AddScoped<IDatabaseProvisioner, MySqlProvisioner>();
}

// El motor "Mongo" tiene DOS implementaciones y se registra exactamente una: el
// factory resuelve por la propiedad Engine, así que registrar ambas dejaría la
// elección al orden de la colección de DI —invisible y frágil—.
//
// La activa es la API externa del equipo; el provisioner local con driver nativo
// queda como camino de vuelta (Enabled=false) y como única forma de seguir
// operando las bases de Mongo creadas antes de la migración, que viven en el
// servidor propio y no existen en esa API.
if (remoteMongo.Enabled)
{
    // HttpClient tipado por la misma razón que el proveedor de DNS: handler
    // compartido y reciclado, sin agotar sockets ni quedarse pegado a una IP
    // vieja. La cabecera de autenticación se pone acá una sola vez.
    // Ver la nota del bloque de MySQL: clase concreta, no la interfaz.
    builder.Services.AddHttpClient<RemoteMongoProvisioner>((sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<RemoteMongoSettings>>().Value;

        // BaseAddress DEBE terminar en "/": si no, Uri descarta el último
        // segmento al combinar con la ruta relativa. Mismo detalle que ya mordió
        // en la configuración de Cloudflare.
        var baseUrl = settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/";

        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Add("X-API-Key", settings.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        // Timeout corto: es una dependencia externa dentro del ciclo de un
        // request del usuario. Fallar rápido y revertir la reserva es mejor que
        // dejar la petición colgada hasta el timeout por defecto de 100 segundos.
        client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
    });

    builder.Services.AddScoped<IDatabaseProvisioner>(
        sp => sp.GetRequiredService<RemoteMongoProvisioner>());
}
else
{
    builder.Services.AddScoped<IDatabaseProvisioner, MongoProvisioner>();
}

// Job que mantiene CurrentSizeMB al día contra el tamaño real de cada motor.
// Es Singleton (todo BackgroundService lo es), así que abre su propio scope de
// DI en cada ciclo para poder usar los servicios Scoped de arriba. Ver
// docs/bugs.md ítem 25; se apaga con Provisioning:SizeMonitor:Enabled=false.
builder.Services.AddHostedService<DatabaseSizeMonitor>();


builder.Services.AddAuthentication(options =>
    {
        // Por defecto, la API se protege con JWT Bearer.
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    // JWT emitido por el propio backend.
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSettings.Key))
        };
    })
    // Cookie temporal donde los handlers OAuth depositan la identidad externa.
    .AddCookie("External", options =>
    {
        options.Cookie.Name = "Colmena.External";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    // Google OAuth. ClientId/Secret desde configuración/secrets (nunca hardcode).
    .AddGoogle("Google", options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
        options.SignInScheme = "External"; // la identidad se guarda en la cookie temporal
    })
    // GitHub OAuth. Se pide scope "user:email" para intentar obtener el correo,
    // que puede venir privado (el handler llama a /user/emails cuando aplica).
    .AddGitHub("GitHub", options =>
    {
        options.ClientId = builder.Configuration["Authentication:GitHub:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"] ?? string.Empty;
        options.SignInScheme = "External";
        options.Scope.Add("user:email");
    });

// ---------------------------------------------------------------------------
// ForwardedHeaders: detrás de un reverse proxy (nginx, IIS, load balancer),
// el backend recibe la petición como HTTP plano aunque el cliente haya usado
// HTTPS. Sin esto, ASP.NET Core arma el `redirect_uri` de OAuth con esquema
// "http" (Google lo rechaza con redirect_uri_mismatch, porque el registrado
// en Google Cloud Console es "https") y además el rate limiting por IP
// (más abajo) particiona todo por la IP del proxy en vez de la del cliente.
// KnownNetworks/KnownProxies se vacían porque el proxy real no está en
// localhost; esto asume que el backend SOLO es alcanzable a través del
// proxy (nunca directo desde internet) — si eso cambia, hay que restringir
// estas listas a la IP real del proxy en vez de vaciarlas.
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

// ---------------------------------------------------------------------------
// CORS: orígenes permitidos del frontend (pruebas locales y despliegue).
// Vienen de appsettings.json (Cors:AllowedOrigins) en vez de hardcodeados, así
// se pueden agregar/quitar orígenes por ambiente (appsettings.Development.json,
// variables de entorno, etc.) sin tocar código. Los orígenes no llevan barra
// final; el navegador compara el Origin exacto.
// ---------------------------------------------------------------------------
const string FrontendCorsPolicy = "FrontendCors";
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

if (corsAllowedOrigins.Length == 0)
{
    throw new InvalidOperationException(
        "Falta configurar Cors:AllowedOrigins en appsettings.json (al menos un origen).");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.WithOrigins(corsAllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ---------------------------------------------------------------------------
// Rate limiting (nativo de .NET). Protege contra abuso/fuerza bruta.
//   - Política "auth": estricta, para login/registro (por IP).
//   - Política "oauth": intermedia, para los flujos de redirect OAuth (por IP).
//   - Política "db-provisioning": para crear/operar BDs físicas (por UserId).
//   - GlobalLimiter: límite general de seguridad por IP para toda la API.
// La partición usa la IP remota; detrás de un proxy inverso (despliegue),
// ForwardedHeaders (configurado arriba) ya resuelve la IP real del cliente.
// ---------------------------------------------------------------------------
const string AuthRateLimitPolicy = "auth";
const string OAuthRateLimitPolicy = "oauth";
const string DbProvisioningRateLimitPolicy = "db-provisioning";
const string DnsRateLimitPolicy = "dns";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Política estricta para endpoints de autenticación: 10 intentos/min por IP.
    options.AddPolicy(AuthRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0 // sin cola: al superar el límite, se rechaza de una
            }));

    // Política intermedia para los 4 endpoints OAuth (login + callback de
    // Google/GitHub): antes solo los cubría el límite global (100/min/IP), 10
    // veces más laxo que el resto de la autenticación. No validan credenciales
    // directamente, pero conviene acotar el abuso del flujo de redirect. 20/min
    // por IP deja margen para reintentos legítimos (un login completo = 2
    // requests: /login redirige y /callback vuelve) sin ser tan estricto como
    // "auth" (10/min), que además comparte partición con login/registro.
    options.AddPolicy(OAuthRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit =  20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Política para POST /databases: crear BDs físicas es costoso (conecta al
    // motor real, ejecuta DDL). Se particiona por UserId (claim del JWT) en vez
    // de IP, porque el endpoint ya requiere autenticación y el abuso relevante
    // es "un usuario crea BDs en bucle", no solo una IP. 5 creaciones/min es
    // generoso para uso normal (el login ya crea la primera automáticamente).
    options.AddPolicy(DbProvisioningRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst(JwtClaimNames.UserId)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Política para las escrituras de /dns. Cada POST/PUT/DELETE dispara una
    // llamada a una API externa con cuota propia (Cloudflare limita a 1200
    // requests cada 5 minutos por cuenta, compartida por TODA la plataforma), así
    // que el abuso relevante no es "este usuario se hace daño a sí mismo" sino
    // "este usuario agota la cuota de todos". Se particiona por UserId igual que
    // db-provisioning —el endpoint ya exige autenticación— y se deja en 10/min,
    // el doble que el aprovisionamiento de BDs porque una operación de DNS es
    // mucho más barata y es normal crear varios subdominios seguidos.
    options.AddPolicy(DnsRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst(JwtClaimNames.UserId)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Límite global por IP para el resto de la API: 100 peticiones/min.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Respuesta uniforme al rechazar (mismo formato {status,error} que el resto).
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // Informa al cliente cuántos segundos esperar, si el límite lo expone.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        }

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"status\":429,\"error\":\"Demasiadas solicitudes. Inténtalo más tarde.\"}",
            token);
    };
});

var app = builder.Build();

// Debe ir lo primero posible: todo lo que sigue (HttpsRedirection, generación
// de redirect_uri de OAuth, rate limiting por IP, autenticación) depende de
// que HttpContext.Request.Scheme / Connection.RemoteIpAddress reflejen los
// valores reales del cliente y no los del salto interno con el proxy.
app.UseForwardedHeaders();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.MapOpenApi();
    // Swagger UI sobre el documento OpenAPI generado, disponible en /swagger.
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Colmena API"));
app.MapScalarApiReference();


app.UseHttpsRedirection();

// CORS debe ir antes de autenticación/autorización.
app.UseCors(FrontendCorsPolicy);

// Rate limiter: aplica el límite global y habilita las políticas nombradas.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
