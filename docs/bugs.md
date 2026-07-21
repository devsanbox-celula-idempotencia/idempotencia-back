# Bugs y hallazgos — análisis de código

> Generado el 2026-07-21 analizando `Controllers/`, `Services/`, `Repository/`,
> `Provisioners/`, `Middleware/`, `Program.cs` y `appsettings.json`. Incluye
> tipo de problema, severidad y solución propuesta.

---

### 1. Token JWT viajando en la query string del redirect de OAuth
**Tipo:** Seguridad (exposición de credenciales/tokens).
**Dónde:** [`Services/OAuthRedirectBuilder.cs:17-28`](../Services/OAuthRedirectBuilder.cs#L17-L28), usado desde [`Controllers/AuthController.cs:72`](../Controllers/AuthController.cs#L72).
**Problema:** `BuildSuccess` arma la URL de retorno al frontend con el `token`
JWT completo como parámetro de query (`.../oauth/callback?token=...`). Las URLs
con query string quedan en: historial del navegador, logs de acceso del
servidor/proxy, y el header `Referer` si la página de callback carga cualquier
recurso de terceros. Un token de sesión filtrado por cualquiera de esas vías
permite secuestrar la cuenta hasta que expire.
**Solución propuesta:**
- Opción mínima: usar el **fragmento** de la URL (`#token=...`) en vez de query
  string — el fragmento no se envía al servidor ni queda en logs de acceso.
- Opción más robusta: el callback genera un **código de un solo uso** de corta
  vida, lo pasa por query string, y el frontend lo canjea por el JWT real
  mediante una llamada `POST` separada.

---

### 2. Endpoints OAuth (`/auth/google/login`, `/auth/github/login` y sus callbacks) sin rate limiting dedicado
**Tipo:** Seguridad / abuso.
**Dónde:** [`Controllers/AuthController.cs:48-64`](../Controllers/AuthController.cs#L48-L64).
**Problema:** `/auth/register` y `/auth/login` tienen `[EnableRateLimiting("auth")]`
(10 intentos/min/IP). Los 4 endpoints OAuth no tienen ese atributo y solo
quedan cubiertos por el límite global (100 req/min/IP), 10 veces más permisivo.
Esto los deja más expuestos a abuso de flujo (aunque el impacto es menor porque
no validan credenciales directamente).
**Solución propuesta:** agregar `[EnableRateLimiting("auth")]` a los 4 métodos,
o crear una política intermedia si 10/min es demasiado estricto para un flujo
de redirect legítimo.

---

### 2b. Auto-actualización continua de `routes.md` (pedido en `docs/claude.md`, Agente 2)
**Tipo:** Proceso / documentación, no un bug de código.
**Problema:** No existe un mecanismo que detecte cambios de rutas y actualice
`docs/routes.md` automáticamente; Claude Code no mantiene procesos en segundo
plano vigilando el repo entre sesiones.
**Solución aplicada:** se agregó una convención en `CLAUDE.md` (raíz del
proyecto) para que cualquier sesión futura de Claude Code actualice
`docs/routes.md` y `docs/API.md` en el mismo cambio que modifique
`Controllers/`. Si se requiere automatización real sin intervención de un
agente, la alternativa es un hook de pre-commit o un check de CI que falle si
`Controllers/**` cambió sin tocar `docs/routes.md`.

---

### 3. Secretos reales en texto plano en `appsettings.json`
**Tipo:** Seguridad / buenas prácticas de configuración.
**Dónde:** [`appsettings.json`](../appsettings.json) (raíz del proyecto).
**Problema:** el archivo contiene, en texto plano: la contraseña del usuario
`sa` de SQL Server, la clave de firma JWT, los `ClientSecret` de Google y
GitHub OAuth, y las credenciales de administrador de MySQL y MongoDB — todas
apuntando a hosts reales (`46.224.101.88`, `100.99.206.50`). Se verificó que
el archivo **no está actualmente en el historial de git** (fue removido del
tracking en el commit `04c5b91` antes de que se agregaran estos valores), pero
sigue siendo una mala práctica tenerlos en un archivo plano dentro del
working directory de cualquier máquina con el repo clonado.
**Solución propuesta:**
- Mover todos los secretos a **User Secrets** (`dotnet user-secrets`) en
  desarrollo y a variables de entorno / Azure Key Vault / un vault equivalente
  en producción.
- Dejar en `appsettings.json` solo placeholders o la estructura sin valores.
- Como higiene adicional (no por leak confirmado): rotar la contraseña del
  `sa`, la clave JWT y los secrets OAuth, ya que están en texto plano en al
  menos una máquina de desarrollo.

---

### 4. Dependencia `Microsoft.OpenApi` con vulnerabilidad conocida (alta severidad)
**Tipo:** Dependencia insegura.
**Dónde:** `idempotencia.csproj` (transitiva vía `Microsoft.AspNetCore.OpenApi 10.0.9`).
**Problema:** `dotnet build` reporta:
```
warning NU1903: Package 'Microsoft.OpenApi' 2.0.0 has a known high severity
vulnerability, https://github.com/advisories/GHSA-v5pm-xwqc-g5wc
```
**Solución propuesta:** actualizar `Microsoft.AspNetCore.OpenApi` (y/o fijar
`Microsoft.OpenApi` a una versión parcheada) y volver a correr
`dotnet list package --vulnerable` para confirmar que desaparece.

---

### 5. `CurrentSizeMB` sin tipo de columna SQL explícito (EF Core)
**Tipo:** Bug latente de datos (truncamiento silencioso).
**Dónde:** [`Models/ProvisionedDatabaseInfo.cs`](../Models/ProvisionedDatabaseInfo.cs), mapeado en [`Data/ColmenaDbContext.cs`](../Data/ColmenaDbContext.cs).
**Problema:** al arrancar la app, EF Core emite:
```
warn: No store type was specified for the decimal property 'CurrentSizeMB'...
This will cause values to be silently truncated if they do not fit in the
default precision and scale.
```
Como el `DbSet` es `HasNoKey()` + `ToView(null)` (solo lectura de un SP), el
riesgo real es que valores devueltos por `sp_GetUserDatabases` con más
decimales/dígitos de los que EF asume por defecto se trunquen silenciosamente
al mapear el resultado, sin ningún error visible.
**Solución propuesta:** especificar explícitamente el tipo en
`OnModelCreating`, por ejemplo:
```csharp
e.Property(p => p.CurrentSizeMB).HasColumnType("decimal(10,2)");
```
(ajustar precisión/escala a lo que realmente devuelve el SP).

---

### 6. Archivo `idempotencia.http` referencia un endpoint inexistente
**Tipo:** Limpieza / archivo obsoleto.
**Dónde:** [`idempotencia.http`](../idempotencia.http).
**Problema:** contiene únicamente `GET {{host}}/weatherforecast/`, remanente
de la plantilla por defecto de .NET. Ese endpoint no existe en ningún
controller del proyecto (solo hay `AuthController` y `DatabasesController`).
Confunde a cualquiera que use el archivo para probar la API manualmente.
**Solución propuesta:** reemplazar el contenido por peticiones reales a
`/auth/register`, `/auth/login` y `/databases`, o eliminar el archivo si ya no
se usa.

---

### 7. Callback de `GetUserId()` puede lanzar `AuthException` sobre un JWT válido pero mal formado
**Tipo:** Robustez / manejo de errores (bajo impacto, defensivo).
**Dónde:** [`Controllers/DatabasesController.cs:57-63`](../Controllers/DatabasesController.cs#L57-L63).
**Problema:** el claim `"UserId"` se busca por nombre de string literal, sin
constante compartida con `JwtTokenService` (que también lo escribe como string
literal `"UserId"` en [`Services/JwtTokenService.cs:31`](../Services/JwtTokenService.cs#L31)).
Si en el futuro alguien cambia el nombre del claim en un solo lugar, el otro
queda desincronizado sin que el compilador avise, y el síntoma sería un 401
confuso ("El token no contiene un identificador de usuario válido") para
tokens que en teoría son válidos.
**Solución propuesta:** extraer el nombre del claim a una constante compartida
(ej. `JwtClaimNames.UserId`) referenciada desde ambos lugares.

---

### 8. Documentación desactualizada respecto al callback OAuth (corregido en esta revisión)
**Tipo:** Documentación desincronizada del código.
**Dónde:** `docs/API.md`, secciones 4.3 y 8 (versión anterior a este análisis).
**Problema:** la documentación decía que el callback OAuth "actualmente...
devuelve el `AuthResponse` como JSON en el navegador" y presentaba la opción de
redirigir al frontend con el token como un cambio *pendiente* de coordinar. En
realidad, `AuthController.ExternalCallback` **ya implementa exactamente esa
opción** (redirige a `{FrontendBaseUrl}/oauth/callback?token=...`) desde que se
agregó `OAuthRedirectBuilder`. Ya se corrigió en `docs/API.md` como parte de
esta revisión.
**Solución aplicada:** ver `docs/API.md` §4.3 y §8, actualizados para reflejar
el comportamiento real, y se dejó registrado el nuevo hallazgo real de
seguridad (token en query string, ítem 1 de este documento) en su lugar.

---

## Resumen por severidad

| # | Hallazgo | Severidad |
|---|---|---|
| 1 | Token JWT en query string del redirect OAuth | 🔴 Alta |
| 3 | Secretos en texto plano en `appsettings.json` | 🔴 Alta (higiene) |
| 4 | Vulnerabilidad conocida en `Microsoft.OpenApi` | 🟠 Media |
| 2 | Sin rate limit dedicado en endpoints OAuth | 🟠 Media |
| 5 | `CurrentSizeMB` sin tipo de columna (truncamiento silencioso) | 🟡 Baja-Media |
| 7 | Claim `"UserId"` duplicado como string literal | 🟡 Baja |
| 6 | `idempotencia.http` con endpoint obsoleto | ⚪ Cosmético |
| 8 | Doc desactualizada sobre callback OAuth | ⚪ Ya corregido en esta revisión |
