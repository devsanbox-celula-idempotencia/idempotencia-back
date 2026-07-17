using System.Text;
using idempotencia.Data;
using idempotencia.Interfaces;
using idempotencia.Middleware;
using idempotencia.Repository;
using idempotencia.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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


builder.Services.AddDbContext<ColmenaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Colmena")));


builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();


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
builder.Services.AddOpenApi();

var app = builder.Build();


app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
