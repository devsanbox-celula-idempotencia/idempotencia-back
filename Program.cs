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


builder.Services.AddDbContext<ColmenaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Colmena")));


builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<IStatisticsRepository, StatisticsRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IOAuthRedirectBuilder, OAuthRedirectBuilder>();

// Aprovisionamiento multi-motor: servicio orquestador + factory + un provisioner
// por motor (patrón Strategy). Se registran todos como IDatabaseProvisioner y el
// factory elige el correcto según el motor pedido.
builder.Services.AddScoped<IDatabaseProvisioningService, DatabaseProvisioningService>();
builder.Services.AddScoped<IDatabaseProvisionerFactory, DatabaseProvisionerFactory>();
builder.Services.AddScoped<IDatabaseProvisioner, SqlServerProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, PostgresProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, MySqlProvisioner>();
builder.Services.AddScoped<IDatabaseProvisioner, MongoProvisioner>();


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
//   - GlobalLimiter: límite general de seguridad por IP para toda la API.
// La partición usa la IP remota; detrás de un proxy inverso (despliegue) hay
// que habilitar ForwardedHeaders para que la IP real llegue correctamente.
// ---------------------------------------------------------------------------
const string AuthRateLimitPolicy = "auth";
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

    // Política para POST /databases: crear BDs físicas es costoso (conecta al
    // motor real, ejecuta DDL). Se particiona por UserId (claim del JWT) en vez
    // de IP, porque el endpoint ya requiere autenticación y el abuso relevante
    // es "un usuario crea BDs en bucle", no solo una IP. 5 creaciones/min es
    // generoso para uso normal (el login ya crea la primera automáticamente).
    options.AddPolicy(DbProvisioningRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("UserId")?.Value
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


app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Swagger UI sobre el documento OpenAPI generado, disponible en /swagger.
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Colmena API"));
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

// CORS debe ir antes de autenticación/autorización.
app.UseCors(FrontendCorsPolicy);

// Rate limiter: aplica el límite global y habilita las políticas nombradas.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
