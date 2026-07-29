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


builder.Services.AddDbContext<ColmenaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Colmena")));


builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<IStatisticsRepository, StatisticsRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IOAuthRedirectBuilder, OAuthRedirectBuilder>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();

// Aprovisionamiento multi-motor: servicio orquestador + factory + un provisioner
// por motor (patrón Strategy). Se registran todos como IDatabaseProvisioner y el
// factory elige el correcto según el motor pedido.
builder.Services.AddScoped<IDatabaseProvisioningService, DatabaseProvisioningService>();
builder.Services.AddScoped<IDatabaseProvisionerFactory, DatabaseProvisionerFactory>();
builder.Services.AddScoped<IDatabaseProvisioner, SqlServerProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, PostgresProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, MySqlProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, MongoProvisioner>();

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
