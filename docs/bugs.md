# Bugs y hallazgos — análisis de código

> Generado el 2026-07-21 analizando `Controllers/`, `Services/`, `Repository/`,
> `Provisioners/`, `Middleware/`, `Program.cs` y `appsettings.json`. Incluye
> tipo de problema, severidad, **estado** y solución propuesta.
>
> **Estados posibles:** 🔴 Abierto · 🟡 Fix entregado (pendiente confirmar que
> se aplicó) · 🟢 Resuelto (confirmado) · 🔵 Resuelto parcialmente. Cuando
> confirmes que aplicaste un fix, dilo en el chat y se actualiza el estado acá
> mismo en el mismo cambio — no queda "flotando" sin reflejarse.

---

### 1. Token JWT viajando en la query string del redirect de OAuth
**Estado:** 🔴 Abierto.
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
**Estado:** 🟢 Resuelto (2026-07-23).
**Tipo:** Seguridad / abuso.
**Dónde:** [`Controllers/AuthController.cs:48-64`](../Controllers/AuthController.cs#L48-L64).
**Problema:** `/auth/register` y `/auth/login` tienen `[EnableRateLimiting("auth")]`
(10 intentos/min/IP). Los 4 endpoints OAuth no tienen ese atributo y solo
quedan cubiertos por el límite global (100 req/min/IP), 10 veces más permisivo.
Esto los deja más expuestos a abuso de flujo (aunque el impacto es menor porque
no validan credenciales directamente).
**Solución aplicada (2026-07-23):** se creó una política intermedia dedicada
`oauth` en [`Program.cs`](../Program.cs) (20 peticiones/min por IP, partición
por IP porque el flujo aún no tiene JWT) y se aplicó
`[EnableRateLimiting("oauth")]` a los 4 métodos OAuth de
[`Controllers/AuthController.cs`](../Controllers/AuthController.cs)
(`GoogleLogin`, `GoogleCallback`, `GitHubLogin`, `GitHubCallback`). Se
prefirió una política propia en vez de reutilizar `auth` (10/min) para no
mezclar la partición del redirect legítimo con la de login/registro y dar
margen a reintentos del proveedor. **Nota:** cambio de código verificado por
lectura; falta un `dotnet build`/arranque para confirmarlo en ejecución.

---

### 2b. Auto-actualización continua de `routes.md` (pedido en `docs/claude.md`, Agente 2)
**Estado:** 🔵 Resuelto parcialmente.
**Tipo:** Proceso / documentación, no un bug de código.
**Problema:** No existe un mecanismo que detecte cambios de rutas y actualice
`docs/routes.md` automáticamente; una sesión de Claude no mantiene procesos en
segundo plano vigilando el repo entre sesiones por sí sola.
**Solución aplicada (parte 1, convención manual):** se agregó una regla en
`CLAUDE.md` (raíz del proyecto) para que cualquier sesión futura actualice
`docs/routes.md`, `docs/API.md` y `docs/bugs.md` en el mismo cambio que
modifique `Controllers/`, y que registre cada sesión en `docs/claude.md`.
**Solución aplicada (parte 2, automatización real):** se configuró una tarea
programada (`scheduled task` de Cowork) que corre en segundo plano sin
necesidad de que el usuario abra una conversación — ver la entrada
correspondiente en `docs/claude.md` para el ID/cadencia exacta. En cada
corrida: compara `Controllers/` contra lo documentado en `routes.md`/`API.md`,
y el estado de cada ítem de `bugs.md` contra el código actual (ej.: si un fix
propuesto ya fue aplicado), actualizando los tres documentos y dejando
constancia de qué cambió. Sigue habiendo un límite real: la tarea corre con la
cadencia configurada (no es un watcher continuo en tiempo real), y no puede
ejecutar SQL contra la BD real para confirmar fixes de Stored Procedures (ver
nota de conectividad en `routes.md`) — esos deben confirmarse manualmente en
el chat.

---

### 3. Secretos reales en texto plano en `appsettings.json`
**Estado:** 🔴 Abierto.
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

**Actualización 2026-07-23 (corrida automática de mantenimiento de
documentación):** la sección `Email` de `appsettings.json` (antes con
placeholders, ver ítem 19) ahora también tiene una cuenta SMTP real en texto
plano (`Username`/`Password` de una App Password de Gmail) — se suma a la
lista de secretos reales ya presentes en este archivo. Mismo tratamiento que
el resto: no versionado en git, pero sigue siendo mala práctica tenerlo en
texto plano en el working directory.

---

### 4. Dependencia `Microsoft.OpenApi` con vulnerabilidad conocida (alta severidad)
**Estado:** 🟡 Fix aplicado (pin a 2.7.5 en el `.csproj`; pendiente confirmar con `dotnet restore`+`dotnet list package --vulnerable`).
**Tipo:** Dependencia insegura.
**Dónde:** `idempotencia.csproj` (transitiva vía `Microsoft.AspNetCore.OpenApi 10.0.9`).
**Problema:** `dotnet build` reporta:
```
warning NU1903: Package 'Microsoft.OpenApi' 2.0.0 has a known high severity
vulnerability, https://github.com/advisories/GHSA-v5pm-xwqc-g5wc
```
**Solución aplicada (2026-07-23):** se agregó un `PackageReference` explícito
a `Microsoft.OpenApi` **2.7.5** en [`idempotencia.csproj`](../idempotencia.csproj)
(primera versión parcheada de la línea 2.x según el aviso; sobrescribe la
2.0.0 transitiva que traía `Microsoft.AspNetCore.OpenApi`). Se verificó que la
API que usa [`OpenApi/BearerSecuritySchemeTransformer.cs`](../OpenApi/BearerSecuritySchemeTransformer.cs)
(`IOpenApiSecurityScheme`, `OpenApiSecuritySchemeReference`) sigue presente en
2.7.5, así que el pin no rompe la compilación. **Pendiente:** correr
`dotnet restore` + `dotnet list package --vulnerable` en la máquina del
usuario para confirmar que el aviso `NU1903` desaparece (no se pudo ejecutar
en el entorno de esta sesión: sin SDK de .NET ni salida de red al instalador).

---

### 5. `CurrentSizeMB` sin tipo de columna SQL explícito (EF Core)
**Estado:** 🟢 Resuelto (2026-07-23).
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
**Solución aplicada (2026-07-23):** se agregó
`e.Property(p => p.CurrentSizeMB).HasColumnType("decimal(10,2)")` en
[`Data/ColmenaDbContext.cs`](../Data/ColmenaDbContext.cs) tanto para
`ProvisionedDatabaseInfo` (listado) como para `ProvisionedDatabaseDetail`
(detalle) — ambos exponen el decimal `CurrentSizeMB`. Verificado por lectura;
falta arrancar la app para confirmar que el warning de EF ya no aparece.

---

### 6. Archivo `idempotencia.http` referencia un endpoint inexistente
**Estado:** 🟢 Resuelto (2026-07-23).
**Tipo:** Limpieza / archivo obsoleto.
**Dónde:** [`idempotencia.http`](../idempotencia.http).
**Problema:** contiene únicamente `GET {{host}}/weatherforecast/`, remanente
de la plantilla por defecto de .NET. Ese endpoint no existe en ningún
controller del proyecto (solo hay `AuthController` y `DatabasesController`).
Confunde a cualquiera que use el archivo para probar la API manualmente.
**Solución aplicada (2026-07-23):** se reescribió
[`idempotencia.http`](../idempotencia.http) con peticiones reales a los 13
endpoints de negocio (registro, login, ambos flujos OAuth, y el CRUD completo
de `/databases` incluyendo detalle/desactivar/eliminar/reset-password y
`/statistics`), con una variable `@token` que reutiliza el JWT del login. Ya
no queda ninguna referencia a `/weatherforecast/`.

---

### 7. Callback de `GetUserId()` puede lanzar `AuthException` sobre un JWT válido pero mal formado
**Estado:** 🟢 Resuelto (2026-07-23).
**Tipo:** Robustez / manejo de errores (bajo impacto, defensivo).
**Dónde:** [`Controllers/DatabasesController.cs:57-63`](../Controllers/DatabasesController.cs#L57-L63).
**Problema:** el claim `"UserId"` se busca por nombre de string literal, sin
constante compartida con `JwtTokenService` (que también lo escribe como string
literal `"UserId"` en [`Services/JwtTokenService.cs:31`](../Services/JwtTokenService.cs#L31)).
Si en el futuro alguien cambia el nombre del claim en un solo lugar, el otro
queda desincronizado sin que el compilador avise, y el síntoma sería un 401
confuso ("El token no contiene un identificador de usuario válido") para
tokens que en teoría son válidos.
**Solución aplicada (2026-07-23):** se creó la constante
[`Services/JwtClaimNames.cs`](../Services/JwtClaimNames.cs)
(`JwtClaimNames.UserId`) y se reemplazó el string literal `"UserId"` en los
tres lugares que lo usaban: la emisión en
[`Services/JwtTokenService.cs`](../Services/JwtTokenService.cs), la lectura en
[`Controllers/DatabasesController.cs`](../Controllers/DatabasesController.cs)
y la partición del rate limiter `db-provisioning` en
[`Program.cs`](../Program.cs). Ahora el nombre del claim es un único punto de
verdad y el compilador obliga a mantenerlos en sync. Verificado por lectura;
falta un `dotnet build` para confirmar la compilación.

---

### 8. Documentación desactualizada respecto al callback OAuth (corregido en esta revisión)
**Estado:** 🟢 Resuelto.
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

### 9. `sp_GetLoginByEmail` referencia una tabla `Roles` que no existe en el esquema real — CONFIRMADO en vivo
**Estado:** 🟢 Resuelto (confirmado en vivo). En el log de `dotnet watch` del
2026-07-21 se ve `EXEC sp_GetLoginByEmail @Email` ejecutando con éxito
(`info: ... Executed DbCommand`), ya sin el `SqlException: Invalid object
name 'Roles'` que fallaba antes — el fix se aplicó contra la BD real. De paso
se confirmó en el mismo log que `sp_RegisterUser` y `sp_GetUserDatabases`
también ejecutan sin error contra el esquema real.
**Tipo:** Bug de datos (SP desincronizado con el esquema real). Severidad alta:
rompe `POST /auth/login` para el 100% de los intentos.
**Dónde:** SP `sp_GetLoginByEmail` en SQL Server (no versionado en el repo, ver
`CLAUDE.md` sobre arquitectura database-centric); consumido desde
[`Repository/UserRepository.cs:20-31`](../Repository/UserRepository.cs#L20-L31)
y mapeado a [`Models/LoginInfo.cs`](../Models/LoginInfo.cs).
**Cómo se detectó:** se corrió `dotnet run` contra la base real
(`100.99.206.50`) y `POST /auth/login` lanzó `SqlException: Invalid object
name 'Roles'` (Error 208). Se pidió el texto del SP (`sp_helptext
'sp_GetLoginByEmail'`) y se confirmó la causa:
```sql
SELECT  u.UserId, u.Email, u.FullName, u.PasswordHash,
        u.IsActive, r.Name AS RoleName
FROM Users u
INNER JOIN Roles r ON r.RoleId = u.RoleId   -- Users no tiene RoleId; no existe tabla Roles
WHERE u.Email = @Email;
```
Según el diagrama ER real, `Users` tiene `Role` como columna string directa
(no normalizada en una tabla `Roles`). El SP quedó escrito para un diseño
distinto al que existe hoy.
**Problema secundario (se manifestaría después de arreglar el join):** el SP
devuelve la columna como `RoleName`, pero `Models/LoginInfo.cs` tiene la
propiedad `Role`. EF Core mapea `FromSqlRaw` por nombre de columna exacto, así
que con solo quitar el join seguiría fallando (`the required column 'Role' was
not present in the results of a 'FromSql' operation`).
**Solución propuesta (aplicar en el servidor SQL, no versionado en git):**
```sql
CREATE OR ALTER PROCEDURE sp_GetLoginByEmail
    @Email NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT  u.UserId, u.Email, u.FullName, u.PasswordHash,
            u.IsActive, u.Role
    FROM Users u
    WHERE u.Email = @Email;
END;
```
**Actualización:** `sp_RegisterUser` se confirmó en vivo funcionando
correctamente (mismo log del 2026-07-21: `EXEC sp_RegisterUser` ejecuta sin
error y el registro completa el flujo de auto-aprovisionamiento MySQL) — no
tiene el mismo bug.

**Actualización 2026-07-22 — `sp_UpsertExternalLogin` CONFIRMADO con el mismo
bug (y uno adicional):** al probar el login OAuth de Google en QA (una vez ya
corregido el `redirect_uri_mismatch` del ítem 17), el callback devolvía
`?error=Ocurrió un error al procesar la solicitud.` — el mensaje genérico que
`ApiExceptionMapper` usa para un `SqlException` con `Number < 50000` (no es
una regla de negocio, es un error real de SQL). Se pidió el `sp_helptext` y
se confirmó:
1. Mismo problema que `sp_GetLoginByEmail`: `INNER JOIN Roles r ON r.RoleId =
   u.RoleId` y `INSERT INTO Users (RoleId, ...) SELECT RoleId ... FROM Roles
   WHERE Name = 'Student'` — la tabla `Roles` no existe; `Users.Role` es
   columna string directa.
2. **Bug adicional que `sp_GetLoginByEmail` no tenía**: el SELECT final
   devolvía la columna como `RoleName`, pero `Models/UserIdentity.cs` (el
   tipo al que EF mapea el resultado de `sp_UpsertExternalLogin` vía
   `FromSqlRaw`) espera la propiedad `Role`. Con solo quitar el `JOIN`
   seguiría fallando por este segundo motivo.

**Solución entregada** (`CREATE OR ALTER PROCEDURE sp_UpsertExternalLogin`,
pasada al usuario en el chat): reemplaza el `INSERT`/`JOIN` contra `Roles`
por `Users.Role` directo (`VALUES ('Student', ...)`), y el SELECT final
devuelve `u.Role` en vez de `r.Name AS RoleName`. **Pendiente:** confirmar en
vivo tras aplicar el `ALTER PROCEDURE` en la base real que el login OAuth de
Google/GitHub completa sin error.

---

### 10. Cuota de almacenamiento (`MaxStorageMB`) no se hace cumplir para Postgres/MySQL/Mongo
**Estado:** 🔴 Abierto (diseño propuesto, no implementado).
**Tipo:** Control de seguridad incompleto (requisito de negocio: "cada BD
tendrá un peso máximo estricto... se debe validar este espacio antes de
permitir nuevas escrituras").
**Dónde:** [`Provisioners/SqlServerProvisioner.cs:46-49`](../Provisioners/SqlServerProvisioner.cs#L46-L49)
vs. [`Provisioners/MySqlProvisioner.cs`](../Provisioners/MySqlProvisioner.cs),
[`Provisioners/PostgresProvisioner.cs`](../Provisioners/PostgresProvisioner.cs),
[`Provisioners/MongoProvisioner.cs`](../Provisioners/MongoProvisioner.cs).
**Problema:** SQL Server sí aplica la cuota de forma nativa y estricta
(`MAXSIZE = {maxStorageMb}MB` en el `CREATE DATABASE`): el motor mismo
rechaza escrituras que excedan el tope. MySQL, PostgreSQL y MongoDB **no
tienen un equivalente nativo de "tamaño máximo por base de datos"** — no hay
ningún `CREATE DATABASE ... MAXSIZE` en esos motores. Por diseño arquitectónico
(el backend es un mediador, no un proxy de las consultas del estudiante sobre
su propia BD), tampoco hay forma de interceptar cada escritura del usuario
para validarla en el momento.
**Por qué no se implementó ya en esta revisión:** requiere un job/servicio
corriendo continuamente contra bases de datos reales para poder probarse, y
este entorno de análisis no tiene conectividad a esos motores (ver
`routes.md`). Escribir el enforcement sin poder ejecutarlo contra una BD real
generaría falsa confianza.
**Solución propuesta (siguiente paso concreto):**
- Un `IHostedService` (`DatabaseQuotaMonitor`) que cada N minutos:
  - MySQL/Postgres: consulta tamaño real vía
    `SELECT SUM(data_length+index_length) FROM information_schema.tables
    WHERE table_schema = @dbName` (MySQL) o
    `SELECT pg_database_size(@dbName)` (Postgres).
  - Mongo: `db.runCommand({dbStats: 1})` → campo `dataSize`.
  - Si el tamaño supera `MaxStorageMB`, revoca privilegios de escritura
    (`REVOKE INSERT, UPDATE ON db.* FROM user` / `REVOKE INSERT ON DATABASE ...`)
    y actualiza `ProvisionedDatabases.Status` a algo como `'OverQuota'` (nuevo
    valor; requiere una nueva SP de catálogo, p. ej. `sp_MarkDatabaseOverQuota`).
  - Cuando vuelva a estar bajo cuota (borró datos), restaurar privilegios.
- Alternativa más simple para MySQL: usar `disk quota` a nivel de sistema de
  archivos si cada BD tiene su propio tablespace/directorio — más frágil y
  fuera del alcance del backend.

---

### 11. Ciclo de vida (TTL): pausado/eliminación automática por inactividad — no implementado
**Estado:** 🔴 Abierto (diseño propuesto, no implementado).
**Tipo:** Control de seguridad faltante (requisito de negocio: "las BD tendrán
una duración máxima de actividad... serán pausadas o eliminadas
automáticamente").
**Dónde:** El catálogo ya tiene las columnas necesarias
([`Models/ProvisionedDatabaseInfo.cs`](../Models/ProvisionedDatabaseInfo.cs):
`LastActivityAt`, `PausedAt`, `DeletedAt`), pero no existe ningún código que
las actualice más allá de la creación inicial. No hay job programado, ni SP de
catálogo para pausar/eliminar, ni lógica en los provisioners para revocar
acceso físico.
**Problema:** sin esto, una BD aprovisionada queda activa indefinidamente aun
si el estudiante nunca vuelve a usarla — contradice el requisito y consume
cupo del servidor compartido sin límite de tiempo.
**Solución propuesta (siguiente paso concreto):**
1. Nuevas SPs de catálogo (a crear en SQL Server, mismo patrón que las
   existentes): `sp_GetIdleDatabases(@InactivityThresholdMinutes)` (devuelve
   filas con `Status='Active' AND LastActivityAt < DATEADD(...)`),
   `sp_PauseDatabase(@DatabaseId)` (marca `PausedAt`, `Status='Paused'`),
   `sp_DeleteDatabase(@DatabaseId)` (marca `DeletedAt`, `Status='Deleted'`).
2. Nuevos métodos en `IDatabaseRepository` que invoquen esas SPs (mismo
   patrón que `ReserveDatabaseAsync`/`ConfirmDatabaseAsync`).
3. Un `IHostedService` (`DatabaseLifecycleJob`) que corra periódicamente
   (ej. cada hora): pide `sp_GetIdleDatabases`, y por cada una:
   - Si superó el umbral de "pausa" (ej. 30 días sin actividad): revoca el
     login/usuario en el motor físico (sin borrar los datos) y llama
     `sp_PauseDatabase`.
   - Si superó el umbral de "eliminación" (ej. 60 días pausada): llama al
     `DropAsync` del provisioner correspondiente y `sp_DeleteDatabase`.
4. `LastActivityAt` necesita alguna señal real de actividad del estudiante
   (hoy nada la actualiza) — definir si viene de logins, de conexiones al
   motor (requeriría auditoría en cada motor), o de un ping explícito del
   frontend.
**Nota:** este es el ítem de mayor esfuerzo pendiente del checklist de
seguridad; se deja documentado en detalle en vez de implementarse a medias sin
poder probarlo contra una base real.

---

### 12. Límite de conexiones concurrentes: cubierto en MySQL/Postgres, sin equivalente nativo en SQL Server/Mongo
**Estado:** 🔵 Resuelto parcialmente (MySQL/Postgres 🟢, ahora configurable
por request; SQL Server/Mongo 🔴).
**Tipo:** Control de seguridad parcialmente implementado.
**Dónde:** [`Provisioners/MySqlProvisioner.cs`](../Provisioners/MySqlProvisioner.cs)
(`WITH MAX_USER_CONNECTIONS`) y
[`Provisioners/PostgresProvisioner.cs`](../Provisioners/PostgresProvisioner.cs)
(`CONNECTION LIMIT`). Implementado en la revisión del 2026-07-21.
**Actualización (misma fecha, a pedido del usuario):** el valor ahora es
configurable por el cliente en `POST /databases`
(`CreateDatabaseRequest.MaxConcurrentConnections`, opcional). El backend lo
resuelve en `Services/DatabaseProvisioningService.ResolveMaxConcurrentConnections`:
si no se pide nada, usa el default (`Provisioning:{Engine}:MaxConcurrentConnections`,
5); si se pide, lo acota SIEMPRE a
`Provisioning:{Engine}:MaxConcurrentConnectionsCap` (20) — el cliente nunca
puede desactivar el control pidiendo un número arbitrariamente alto. El valor
efectivamente aplicado vuelve en `CreateDatabaseResponse.MaxConcurrentConnections`.
En SqlServer/Mongo el valor se acepta y se ignora (0 en la respuesta).
**Problema:** SQL Server no tiene un límite nativo de conexiones concurrentes
*por login* (existe `Resource Governor`, pero es a nivel de grupo de carga de
trabajo, no por login individual, y requiere configuración de servidor fuera
del alcance de un `CREATE LOGIN`). MongoDB tampoco expone un límite de
conexiones por usuario (solo `net.maxIncomingConnections` a nivel de servidor
completo).
**Solución propuesta para SQL Server:** un logon trigger a nivel de servidor
que cuente sesiones activas por login y haga `ROLLBACK` si excede el límite:
```sql
CREATE TRIGGER trg_LimitConnectionsPerLogin ON ALL SERVER FOR LOGON
AS
BEGIN
    IF ORIGINAL_LOGIN() LIKE 'usr_colmena_%'
       AND (SELECT COUNT(*) FROM sys.dm_exec_sessions
            WHERE login_name = ORIGINAL_LOGIN() AND is_user_process = 1) > 5
        ROLLBACK;
END;
```
No se aplicó en esta revisión porque un logon trigger mal probado puede
bloquear TODAS las conexiones del servidor (incluida `sa`) si tiene un bug —
requiere probarse directamente contra la BD real antes de desplegarlo.

---

### 13. Usuarios aprovisionados podían ver/conectarse a las BDs de otros usuarios — reportado por el usuario, corregido en SQL Server y Postgres
**Estado:** 🔵 Resuelto parcialmente (SQL Server 🟢, Postgres 🟢, MySQL 🔴
limitación inherente del motor, Mongo 🟢 ya estaba bien por defecto).
**Tipo:** Aislamiento multi-inquilino (fuga de información / control de
acceso).
**Dónde:** [`Provisioners/SqlServerProvisioner.cs`](../Provisioners/SqlServerProvisioner.cs),
[`Provisioners/PostgresProvisioner.cs`](../Provisioners/PostgresProvisioner.cs).
**Problema:** cada estudiante tiene su propia BD, pero por defecto ni SQL
Server ni Postgres restringen qué bases de datos puede *ver* o *a cuáles se
puede conectar* un login/rol nuevo — solo restringen qué puede hacer una vez
adentro. Concretamente:
- **SQL Server:** cualquier login ve los nombres de TODAS las BDs del
  servidor en `sys.databases` (y por lo tanto en el Object Explorer de SSMS),
  aunque no tenga permiso para entrar a ellas.
- **PostgreSQL:** el rol `PUBLIC` (o sea, *cualquier* usuario del servidor)
  tiene privilegio `CONNECT` sobre toda base de datos nueva por defecto — un
  estudiante podía literalmente conectarse (`psql -d BD_de_otro_estudiante`)
  a la BD de otro, aunque después no pudiera leer sus tablas sin más grants.
**Solución aplicada:**
- SQL Server: `DENY VIEW ANY DATABASE TO {login};` justo después de crear el
  login — oculta todas las BDs a las que no tiene acceso explícito (sigue
  viendo `master`/`tempdb` y la suya).
- Postgres: `REVOKE CONNECT ON DATABASE {dbName} FROM PUBLIC;` seguido de
  `GRANT CONNECT ON DATABASE {dbName} TO {login};` justo después de crear la
  BD — ya ningún otro rol puede conectarse a ella, solo el dueño.
**Limitación conocida, no corregida (MySQL):** `information_schema.schemata`
y `SHOW DATABASES` en MySQL listan los NOMBRES de todas las bases del
servidor a cualquier usuario autenticado por diseño del motor — no existe un
equivalente directo a `DENY VIEW ANY DATABASE`/`REVOKE CONNECT` sin recurrir a
vistas personalizadas sobre `information_schema` (complejo, no estándar, fuera
de alcance de esta revisión). Lo que sí sigue firme: el `GRANT ALL PRIVILEGES
ON db.* TO user` ya scoped a su propia BD impide leer/escribir datos de
BDs ajenas — el estudiante puede *ver el nombre* de otras BDs pero no
*entrar* a sus tablas.
**Mongo:** no requirió cambio — un usuario con rol `readWrite` scoped a una
sola BD (como ya hace `MongoProvisioner`) por defecto solo ve esa BD al
correr `show dbs`, MongoDB ya filtra por privilegio en ese comando.

---

### 14. Enumeración de cuentas: `POST /auth/login` distinguía cuentas OAuth-only sin necesitar la contraseña — CORREGIDO, LUEGO REVERTIDO POR DECISIÓN DE PRODUCTO
**Estado:** 🟠 Reabierto a propósito (2026-07-29) — ver "Reversión" al final.
**Tipo:** Seguridad (enumeración de usuarios / fuga de información).
**Dónde:** [`Services/AuthService.cs`](../Services/AuthService.cs) — `LoginAsync`.
**Problema:** el orden de validaciones era: (1) correo no existe → genérico
"Credenciales inválidas" ✅, (2) `PasswordHash` nulo (cuenta creada solo por
OAuth) → mensaje específico **"Esta cuenta usa inicio de sesión externo."**
❌, (3) password incorrecto → genérico ✅, (4) cuenta inactiva → mensaje
específico "La cuenta está inactiva." (este caso es inofensivo porque ya pasó
la verificación de password). El paso (2) ocurría **antes** de verificar la
contraseña, así que cualquiera podía mandar `POST /auth/login` con un correo
cualquiera y **cualquier** contraseña (ni siquiera tenía que ser correcta) y
el mensaje de respuesta le decía si ese correo existe y si es una cuenta
OAuth-only — sin necesitar acertar ninguna credencial. Eso es un vector de
enumeración de cuentas clásico.
**Solución aplicada:** se colapsaron los casos "no existe" / "es OAuth-only
(sin hash)" / "password incorrecto" en una sola condición que siempre
devuelve el mismo mensaje genérico `"Credenciales inválidas."`. El chequeo de
`IsActive` se mantiene después (ya no es explotable: para verlo, el atacante
ya tuvo que acertar la contraseña real).

**Reversión (2026-07-29) — decisión de producto, con el riesgo asumido:** se
volvió a separar el mensaje por caso porque `"Credenciales inválidas."` no le
dice al usuario legítimo qué corregir. `LoginAsync` ahora responde 401 con:

| Caso | Mensaje |
|---|---|
| El correo no está registrado | `No existe una cuenta registrada con ese correo.` |
| Cuenta sin `PasswordHash` (creada por OAuth) | `Esta cuenta se registró con un proveedor externo (Google o GitHub). Inicia sesión con ese proveedor.` |
| Contraseña que no coincide | `La contraseña es incorrecta.` |
| Credenciales correctas, cuenta deshabilitada | `La cuenta está inactiva.` (sin cambios) |

Con esto **vuelve a existir** el vector de enumeración descrito arriba: un
tercero puede confirmar si un correo está registrado, y si es cuenta
OAuth-only, mandando `POST /auth/login` con cualquier contraseña. Lo que
contiene el abuso hoy es únicamente el rate limiting de la política `"auth"`
(`[EnableRateLimiting("auth")]` sobre `Login` y `Register` en
`AuthController`), que limita el ritmo de sondeo pero no lo impide.

Alternativas que quedan sobre la mesa si más adelante se prioriza otra vez la
privacidad: (a) mantener el mensaje genérico en el backend y devolver un
`code` estructurado en el JSON de error solo para sesiones ya autenticadas o
verificadas; (b) conservar el mensaje genérico y mover la ayuda al frontend
(link a "recuperar contraseña" + botones de Google/GitHub siempre visibles
bajo el formulario), que resuelve la misma queja de UX sin revelar nada.
Revertir es barato: basta con volver a colapsar los tres primeros casos en una
sola condición con un mensaje único.

---

### 15. Redirect OAuth expone email/nombre/rol/userId además del token en la query string — hallazgo ampliado (ver ítem 1)
**Estado:** 🔴 Abierto (mismo fix pendiente que el ítem 1).
**Tipo:** Seguridad (exposición de PII).
**Dónde:** [`Services/OAuthRedirectBuilder.cs:17-28`](../Services/OAuthRedirectBuilder.cs#L17-L28).
**Problema:** el ítem 1 documentaba que el JWT viaja en la query string del
redirect OAuth. Auditoría de esta revisión encontró que `BuildSuccess` en
realidad mete **seis** valores en la query string, no solo el token:
`token`, `expiresAt`, `userId`, `email`, `fullName`, `role`. Todos quedan en
historial del navegador, logs de acceso del servidor/proxy, y el header
`Referer` si la página de callback carga algún recurso de terceros — igual
que el token, pero ahora incluyendo el nombre completo y el correo del
usuario en texto plano en la URL.
**Por qué no se corrigió ya:** la solución robusta (código de un solo uso que
el frontend canjea por un `POST` separado) es la misma que ya proponía el
ítem 1 y es un cambio de flujo más grande (nuevo endpoint de canje, estado
temporal del código). Se deja consolidado en el ítem 1 en vez de duplicar la
solución propuesta; ver esa sección para el detalle.
**Mitigación que sí se aplicó (ítem 16, relacionado):** al menos ya no se
agrega un séptimo valor (la contraseña de la BD MySQL auto-aprovisionada) a
esta misma query string — ver ítem 16.

---

### 16. Login por OAuth generaba una BD MySQL con contraseña imposible de entregar (huérfana) — CORREGIDO
**Estado:** 🟢 Resuelto.
**Tipo:** Bug funcional con implicación de seguridad (credencial generada y
perdida sin que el usuario la vea nunca).
**Dónde:** [`Services/AuthService.cs`](../Services/AuthService.cs) —
`ExternalLoginAsync`.
**Problema:** `RegisterAsync`/`LoginAsync` (contraseña) SÍ llamaban
`EnsureMySqlDatabaseAsync` y su `AuthResponse` se devuelve como JSON normal,
así que el campo `mySqlDatabase.password` le llega bien al frontend.
`ExternalLoginAsync` (OAuth) también lo llamaba, pero su `AuthResponse`
**nunca se serializa como JSON** — `AuthController.ExternalCallback` lo pasa
a `OAuthRedirectBuilder.BuildSuccess`, que arma un redirect por query string y
NO reenvía `mySqlDatabase` (con razón: sería agregar la contraseña real de la
BD a la lista de datos ya expuestos del ítem 15). Resultado: en el primer
login por Google/GitHub se creaba de verdad una BD MySQL con una contraseña
real generada por `PasswordGenerator`, el catálogo guardaba solo el **hash**
de esa contraseña (`sp_ConfirmDatabase`), y el valor en texto plano se
descartaba sin que el usuario lo viera jamás — una BD aprovisionada
permanentemente inutilizable, porque no hay (todavía) una función de "resetear
contraseña" para BDs provisionadas.
**Solución inicial (2026-07-22):** se quitó la llamada a
`EnsureMySqlDatabaseAsync` de `ExternalLoginAsync`. Los usuarios que entraban
por OAuth NO obtenían su BD MySQL automáticamente; el frontend debía llamar
`POST /databases` explícitamente tras el callback.

**Solución final (2026-07-23) — auto-aprovisionar + entregar por correo:** a
pedido del equipo, se reactivó el auto-aprovisionamiento en OAuth resolviendo
el nudo de la contraseña por el canal que faltaba: el **correo**.
`ExternalLoginAsync` vuelve a llamar `EnsureMySqlDatabaseAsync` y, si crea la
BD, envía las credenciales completas (host/puerto/BD/usuario/contraseña) al
correo del usuario vía `IEmailService`
([`EmailTemplates.FirstDatabaseCredentials`](../Services/EmailTemplates.cs)),
reutilizando el mismo SMTP del reset de contraseña. La contraseña **no** se
pobla en el `AuthResponse` (se perdería/expondría en el redirect) — el correo
es el único canal. El envío es **no bloqueante**: si el correo falla, el login
continúa, la BD ya existe y el usuario puede regenerar la contraseña con
`POST /databases/{id}/reset-password` (que también la manda por correo), así
que ya no hay riesgo de BD huérfana permanente. El frontend ya **no** necesita
llamar `POST /databases` tras un login OAuth para la BD "principal" (puede
seguir creando BDs adicionales). Ver `docs/API.md` §5.4 (actualizada). Cambio
de código verificado por lectura; falta un `dotnet build`/arranque para
confirmarlo en ejecución.

---

### 17. `redirect_uri` de OAuth viajaba como `http://` en vez de `https://` detrás del reverse proxy de QA — CORREGIDO
**Estado:** 🟢 Resuelto (confirmado en vivo por el usuario, 2026-07-23: tras
desplegar en QA, `redirect_uri` sale como `https://` y el login con Google
completa el flujo sin `redirect_uri_mismatch`).
**Tipo:** Bug de configuración de infraestructura (rompe el login OAuth por
completo en cualquier ambiente detrás de reverse proxy).
**Dónde:** [`Program.cs`](../Program.cs) — faltaba `UseForwardedHeaders`.
**Problema:** en el despliegue de QA (`docs.idempotencia.andrescortes.dev`),
Google devolvía `Error 400: redirect_uri_mismatch` con el detalle
`redirect_uri=http://docs.idempotencia.andrescortes.dev/signin-google`
(confirmado directamente en la pantalla de error de Google). El URI
registrado en Google Cloud Console es `https://...`. Causa: el backend está
detrás de un reverse proxy que termina TLS y le reenvía la petición como HTTP
plano; sin `ForwardedHeaders` configurado, ASP.NET Core no sabía que el
esquema original era `https` y `Microsoft.AspNetCore.Authentication.Google`
armaba el `redirect_uri` del `Challenge` con el esquema que veía (`http`),
que nunca coincide con lo registrado. Este mismo gap ya estaba anotado en
`docs/API.md` (sección de rate limiting) como pendiente para el entorno de
despliegue, pero solo se había evaluado su impacto en la partición de IP del
rate limiter, no en OAuth.
**Solución aplicada:**
```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
// ...
app.UseForwardedHeaders(); // primera línea del pipeline, antes de HttpsRedirection/Auth
```
`KnownNetworks`/`KnownProxies` se vaciaron porque el proxy real de QA no está
en `localhost` (rango de confianza por defecto de ASP.NET Core). Esto asume
que el backend **no es alcanzable directamente** sin pasar por el proxy — si
en algún ambiente eso no es cierto, hay que restringir esas listas a la IP
real del proxy en vez de vaciarlas (vaciarlas por completo permite spoofear
`X-Forwarded-For`/`X-Forwarded-Proto` a quien le pegue directo al backend).
**Efecto secundario positivo:** esto también resuelve, de paso, la limitación
de rate limiting detrás de proxy que ya estaba documentada en `docs/API.md`
(la partición por IP ahora usa la IP real del cliente, no la del proxy).
**Pendiente:** confirmar en vivo contra el despliegue de QA que
`redirect_uri` ya sale como `https://` y que el login con Google completa el
flujo sin `redirect_uri_mismatch`.

---

### 18. Validación de entrada débil en `Email`/`FullName`/`DbName`/`Engine` — endurecida
**Estado:** 🟢 Resuelto.
**Tipo:** Endurecimiento de seguridad / calidad de dato (no era una
vulnerabilidad explotable confirmada, sino una superficie de riesgo:
validación mínima dejaba pasar valores mal formados hacia capas más
sensibles).
**Dónde:** [`DTOs/AuthDtos.cs`](../DTOs/AuthDtos.cs),
[`DTOs/DatabaseDtos.cs`](../DTOs/DatabaseDtos.cs),
[`DTOs/InputNormalization.cs`](../DTOs/InputNormalization.cs) (nuevo),
[`Controllers/AuthController.cs`](../Controllers/AuthController.cs).
**Contexto previo:** el catálogo (SQL Server) ya estaba protegido contra SQL
injection clásica — todas las consultas usan `FromSqlRaw` + `SqlParameter`
tipados, nunca concatenación de strings. El punto más sensible real era
`DbName`/`Engine` en `POST /databases`: esos valores terminan formando parte
de sentencias DDL crudas dentro de cada `IDatabaseProvisioner`
(`CREATE DATABASE`, `CREATE USER`/`CREATE LOGIN`, `GRANT`). Ya se citaban
identificadores por motor (`[corchetes]` en SQL Server, `` `backticks` `` en
MySQL, `"comillas"` en Postgres) como defensa en profundidad, pero la
validación de entrada en el DTO era mínima (`[MaxLength]` nada más) — un
nombre con espacios, comillas o `;` llegaba hasta esa capa antes de ser
neutralizado solo por el escape.
**Solución aplicada:**
- `CreateDatabaseRequest.DbName`: `[RegularExpression(@"^[a-zA-Z][a-zA-Z0-9_]{2,127}$")]`
  — solo letras/números/guion bajo, debe empezar con letra. Rechaza con `400`
  cualquier caracter fuera de ese set antes de tocar la base de datos.
- `CreateDatabaseRequest.Engine`: `[RegularExpression("^(SqlServer|Postgres|MySql|Mongo)$")]`
  — feedback de validación inmediato (antes ya se validaba en
  `DatabaseProvisionerFactory`, pero solo con un `400` genérico más tarde en
  el flujo).
- `RegisterRequest.Email` / `LoginRequest.Email`: además de `[EmailAddress]`,
  se agregó `[RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]` (más
  estricto) y normalización automática (trim + minúsculas) en el setter,
  para que variantes de mayúsculas/espacios no generen cuentas "distintas"
  ni errores de validación confusos.
- `RegisterRequest.FullName`: `[RegularExpression]` que solo admite letras
  (con acentos/ñ vía `\p{L}`), espacios, apóstrofes, guiones y puntos —
  bloquea dígitos y símbolos de control. Los espacios repetidos se colapsan
  automáticamente.
- `AuthController.ResolveExternalLoginAsync` (callback OAuth): se aplicó la
  misma normalización (trim + minúsculas en email, colapso de espacios en
  nombre) a los datos que llegan de Google/GitHub, para consistencia con los
  dos formularios anteriores.
- Nuevo helper compartido `InputNormalization` (`DTOs/InputNormalization.cs`)
  para no duplicar la lógica de trim/normalización entre DTOs.
**Documentación actualizada:** `docs/API.md` (reglas de validación de
`POST /auth/register` y `POST /databases`) y la guía de creación de bases de
datos para Docusaurus.
**Nota:** esto es una capa adicional de defensa en profundidad, no reemplaza
las protecciones existentes (parámetros tipados + escape de identificadores
por motor), que ya eran correctas.

---

### 19. Ciclo de vida manual de bases de datos (detalle, desactivar, eliminar, reset de contraseña) — código completo, pendiente de desplegar
**Estado:** 🟢 Resuelto (confirmado por el usuario, 2026-07-23: los 4 Stored
Procedures ya se desplegaron contra la BD real y el SMTP ya está configurado;
los 4 endpoints funcionan de punta a punta).
**Tipo:** Feature nueva, a pedido del usuario (no es un bug).
**Motivación:** el usuario preguntó cómo ven los usuarios los datos de su BD
(respuesta: conectándose directo al motor con su propio cliente, el backend
nunca es un proxy de datos) y pidió cubrir el caso de credenciales perdidas:
poder ver de nuevo los datos de conexión (sin la contraseña, irrecuperable),
desactivar una BD, eliminarla solo si ya está inactiva, y resetear la
contraseña enviándola por correo en vez de devolverla en la respuesta HTTP.
**Dónde:**
- `Controllers/DatabasesController.cs` — 4 endpoints nuevos:
  `GET /databases/{id}`, `POST /databases/{id}/deactivate`,
  `DELETE /databases/{id}`, `POST /databases/{id}/reset-password`.
- `Services/DatabaseProvisioningService.cs` — orquestación
  (`GetDetailAsync`/`DeactivateAsync`/`DeleteAsync`/`ResetPasswordAsync`).
- `Interfaces/IDatabaseProvisioner.cs` + los 4 provisioners — se agregó
  `Host`/`Port` (propiedades, derivadas de config, no del catálogo) y los
  métodos `ChangePasswordAsync`/`DeactivateAsync` (implementados por motor:
  `ALTER LOGIN ... DISABLE` en SQL Server, `ACCOUNT LOCK` en MySQL, `NOLOGIN`
  en Postgres, vaciar `roles` en Mongo).
- `Interfaces/IEmailService.cs` + `Services/SmtpEmailService.cs` (MailKit) +
  `Services/EmailSettings.cs` + `Services/EmailTemplates.cs` — envío de
  correo nuevo en el proyecto, hoy configurado para SMTP de Gmail/Workspace.
- `Middleware/AppExceptions.cs` — nueva `NotFoundException` (404), usada para
  "no existe o no es tuyo" con el mismo mensaje en ambos casos (evita
  enumeración de IDs ajenos).
- `Models/ProvisionedDatabaseDetail.cs`, `Data/ColmenaDbContext.cs` — nuevo
  tipo sin clave para el resultado de `sp_GetDatabaseDetail` (incluye
  `LoginName`, a diferencia de `ProvisionedDatabaseInfo` del listado).
- [`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql) —
  4 SPs nuevos (no versionados en el repo por diseño, ver `CLAUDE.md`):
  `sp_GetDatabaseDetail`, `sp_DeactivateDatabase`, `sp_DeleteDatabase`,
  `sp_ResetDatabasePassword`.
**Decisiones de diseño (confirmadas con el usuario):**
- Desactivar = revocar acceso físico real (login/usuario deshabilitado en el
  motor), no solo un flag en el catálogo — así una BD "Inactive" realmente no
  es alcanzable aunque alguien tenga las credenciales viejas.
- Eliminar = borrado físico real (`DROP DATABASE`/usuario, usa el `DropAsync`
  que ya existía), irreversible, solo permitido si la BD ya está `Inactive`.
- Reset de contraseña = la contraseña nueva SOLO se entrega por correo, nunca
  en la respuesta HTTP (a diferencia de la creación, donde si se muestra una
  vez en la respuesta) — decisión explícita para no repetir el patrón de
  "credencial en el cuerpo de la respuesta" en un flujo que además implica
  que el usuario ya perdió el control de la anterior.
**Pendiente para que funcione en un ambiente real (originalmente 2 pasos
manuales, ninguno autoejecutable por el backend — ver actualización
2026-07-23 v2 más abajo, el paso de SMTP ya se resolvió):**
1. Correr [`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql)
   contra la base real. **Asume que `ProvisionedDatabases` ya tiene una
   columna `LoginName`** (el script trae instrucciones si no existe). **Sigue
   pendiente** — ver actualización 2026-07-23 (el archivo no está presente en
   el repositorio conectado).
2. ~~Configurar la sección `Email` de `appsettings.json` con una cuenta SMTP
   real~~ — **resuelto** (ver actualización 2026-07-23 v2 abajo):
   `appsettings.json` ya tiene una cuenta SMTP real de Gmail (App Password)
   en vez de placeholders. Recomendado, como higiene adicional, moverlo a
   User Secrets / variables de entorno en vez de dejarlo en texto plano
   (mismo criterio que el ítem 3 de este documento).
3. Confirmar en vivo cada uno de los 4 endpoints contra la BD y un SMTP real.
**Backlog relacionado, no implementado en esta revisión:** no existe
"reactivar" una BD desactivada (`ENABLE`/`ACCOUNT UNLOCK`/`LOGIN` según el
motor) — hoy desactivar es, en la práctica, un paso previo obligatorio hacia
eliminar, no una pausa reversible desde la API.

**Actualización 2026-07-22 (v2) — esquema real confirmado, script corregido:**
al correr la v1 del script contra la base real, `sp_GetDatabaseDetail` falló
con `SQL Error [207]: Invalid column name 'LoginName'`. El usuario compartió
el diagrama ER real: `LoginName`/`PasswordHash` **no viven en
`ProvisionedDatabases`**, sino en una tabla aparte, `DatabaseCredentials`
(`DatabaseId`, `LoginName`, `PasswordHash`, `IsRevoked`, `CreatedAt`,
`RevokedAt`) — relacionada 1-a-muchos con `ProvisionedDatabases`. Se corrigió
[`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql)
(v2) para hacer `JOIN` contra `DatabaseCredentials` filtrando por
`IsRevoked = 0` (la credencial vigente). De paso se aprovechó el diseño real
de la tabla: `sp_ResetDatabasePassword` ahora **revoca la credencial vieja e
inserta una fila nueva** (mismo `LoginName`, hash nuevo) en vez de
sobreescribir — conserva historial completo de credenciales por BD, tal como
sugieren las columnas `IsRevoked`/`RevokedAt`. `sp_DeleteDatabase` también
revoca la credencial vigente al eliminar (higiene, ya no hay login físico
correspondiente). **No se requirieron cambios en el código C#** — el join es
un detalle interno del SP, la firma de parámetros no cambió.

**Actualización 2026-07-23 (corrida automática de mantenimiento de
documentación):** al revisar el repositorio conectado en esta corrida, el
archivo [`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql)
referenciado en este ítem y en `docs/routes.md` (hallazgo 11) **no existe en
el disco** — no hay carpeta `sql/` en el repo, y `sql/` tampoco está en
`.gitignore` (a diferencia de `appsettings.json`, que sí está ignorado pero
sigue presente localmente), así que no es un caso de "no versionado a
propósito pero presente en la copia de trabajo": el script simplemente no
está ahí. Esta tarea no puede ejecutar SQL ni ver el historial de sesiones de
chat anteriores, así que no puede confirmar si el script ya se aplicó contra
la base real y se borró después, si nunca se guardó como archivo (se
entregó solo en el chat de otra sesión), o si se perdió. **Acción
recomendada:** si los 4 SPs (`sp_GetDatabaseDetail`, `sp_DeactivateDatabase`,
`sp_DeleteDatabase`, `sp_ResetDatabasePassword`) ya se aplicaron contra la BD
real, confírmalo en el chat para mover este ítem a 🟢 y dejar constancia; si
no, habrá que regenerar el script (la definición completa de los 4 SPs quedó
documentada en la actualización v2 de este mismo ítem, arriba) antes de poder
desplegarlo.

**Actualización 2026-07-23 (v2, misma corrida automática) — paso 2 del
"Pendiente" ya resuelto:** `appsettings.json` en el repositorio conectado ya
no tiene placeholders en la sección `Email` — tiene una cuenta SMTP real de
Gmail configurada (`Host: smtp.gmail.com`, `Username`, `Password` con formato
de App Password). Del checklist de 3 pasos de este ítem, el paso 1 (SMTP)
queda confirmado por lectura de código/config; **solo sigue pendiente el paso
2** (aplicar el script SQL de los 4 SPs — ver el hallazgo de arriba, el
archivo no está en el repo) **y el paso 3** (confirmar en vivo los 4
endpoints). Este archivo también se sumó a la lista de secretos en texto
plano del ítem 3.

---

### 20. `POST /auth/register` rechazaba un correo de exactamente 150 caracteres (el máximo documentado) — CORREGIDO
**Estado:** 🟢 Resuelto.
**Tipo:** Bug de validación (falso negativo — un dato válido se rechazaba).
**Dónde:** [`DTOs/AuthDtos.cs`](../DTOs/AuthDtos.cs) — `RegisterRequest.Email` /
`LoginRequest.Email`.
**Problema:** el campo tenía tres validadores independientes apilados sobre
la misma propiedad: `[EmailAddress]` (de .NET), un `[RegularExpression]`
propio, y `[MaxLength(150)]`. Los tres deben pasar para que el request sea
válido, pero al ser independientes no había garantía de que coincidieran
exactamente en el límite de 150 caracteres — un correo de longitud máxima
documentada devolvía `400`.
**Solución aplicada:** se sacó la validación de `Email` de los atributos y se
centralizó en un solo lugar: `RegisterRequest`/`LoginRequest` ahora
implementan `IValidatableObject`, y `Validate()` llama a
`EmailValidation.Validate()` (nueva clase en `DTOs/AuthDtos.cs`), que aplica
tres reglas explícitas y en orden — obligatorio, longitud (`<=` 150,
verificado inclusivo), formato (un solo regex, sin `[EmailAddress]`) — cada
una con su propio mensaje. La longitud/formato se resuelven con dos métodos
nuevos en `DTOs/InputNormalization.cs`
(`IsWithinMaxEmailLength`/`IsValidEmailFormat`), que son el único punto de
verdad y quedan aislados para poder testearse sin levantar el pipeline HTTP
completo.
**Por qué en el modelo y no en el servicio:** la validación de formato de
entrada (no una regla de negocio) pertenece al DTO — es lo que ya hacían
`CreateDatabaseRequest`/`FullName` (ítem 18) y mantiene el mismo patrón; así
sigue disparando un `400` de `ValidationProblemDetails` estándar en vez de
tener que envolver esto en una excepción de negocio desde `AuthService`.
**Reconfirmado 2026-07-23:** el equipo de front volvió a reportar el 400 con un
correo de 150 caracteres. Verificado que el código actual ya lo permite
(`InputNormalization.IsWithinMaxEmailLength` usa `<= 150`, inclusivo). Si QA
sigue viéndolo, es contra el ambiente **desplegado**, que va detrás del código
committeado — se resuelve redesplegando. Si aun así fallara con el código
nuevo, revisar del lado de la BD que `@Email` de `sp_RegisterUser` y la columna
`Users.Email` sean `NVARCHAR(150)` y no menos.

---

### 21. `POST /auth/register` aceptaba contraseñas de más de 12 caracteres — CORREGIDO
**Estado:** 🟢 Resuelto.
**Tipo:** Bug de validación (falso positivo — un dato inválido según el
requisito de negocio se aceptaba).
**Reportado por:** ticket de QA "[Bug][Registro] Error al registrar usuario
con contraseña mayor al límite máximo" — grabación adjunta mostrando que el
frontend permite enviar el formulario con una contraseña de más de 12
caracteres.
**Dónde:** [`DTOs/AuthDtos.cs`](../DTOs/AuthDtos.cs) — `RegisterRequest.Password`.
**Problema:** el máximo estaba configurado en 100 caracteres
(`[MaxLength(100)]`), pero el requisito de negocio confirmado es **12
caracteres máximo** (rango válido: 8–12). El backend aceptaba contraseñas de
hasta 100 caracteres sin error, contradiciendo el límite real.
**Solución aplicada:** `[MaxLength(12, ErrorMessage = "La contraseña no
puede superar los 12 caracteres.")]` en `RegisterRequest.Password`. El mínimo
de 8 no se tocó (no se pidió cambiarlo) — el rango válido queda 8–12.
**A propósito NO se tocó `LoginRequest.Password`**: el login no debe volver a
validar longitud contra la política vigente de registro — si en el futuro el
rango cambia de nuevo, o si existieran cuentas creadas bajo un límite previo
distinto, restringir el login por longitud podría bloquear a un usuario que
ya tiene una contraseña válida y hasheada. La longitud solo se controla en el
momento de fijar la contraseña (registro); el login solo verifica el hash.
**Documentación actualizada:** `docs/API.md` (§5.1), `README.md` (tabla de
errores de `/auth/register`), y ambas guías de Docusaurus
(`03-api-referencia.md`, `idempotencia-back-docusaurus.md`) — todas decían
"8–100 caracteres", ahora dicen "8–12".

---

### 22. Connection string `Colmena` apunta a `Database=master` (funciona, pero mala práctica) — reportado por el front
**Estado:** 🔵 Conocido / aceptado (2026-07-23): funciona en vivo; el equipo
decidió dejarlo como está por ahora.
**Tipo:** Configuración / buena práctica (no rompe funcionalidad hoy).
**Dónde:** [`appsettings.json`](../appsettings.json) →
`ConnectionStrings:Colmena` (y `Provisioning:SqlServer:AdminConnectionString`).
**Reporte del front (2026-07-17):** con `Database=master`,
`POST /auth/register`/`login` devolvían 500, con la hipótesis de que los SPs
no existen en `master`.
**Verificación (2026-07-23):** el login se confirmó funcionando **en vivo**
contra ese mismo servidor (`100.99.206.50`) desde la sesión 3 (ver ítem 9), y
siguió funcionando en las sesiones posteriores. Es decir, el esquema (tablas +
SPs) **sí está desplegado en `master`** y la app funciona tal cual. El 500 que
vio el front el 07-17 es anterior a los arreglos de connection string de la
sesión 2 (había un typo `Server=d`) — ya no reproduce. Nota: en SQL Server los
procedimientos con prefijo `sp_` creados en `master` se resuelven desde
cualquier contexto de BD, lo que refuerza que "apuntar a master" hoy no rompe
nada aunque sea poco ortodoxo.
**Decisión (2026-07-23):** el usuario optó por **dejarlo como está**. Cambiar
el `Database` sin migrar primero el esquema completo a la BD destino rompería
todo lo que hoy funciona. Queda como deuda técnica: cuando exista una BD
dedicada con el esquema desplegado, apuntar ahí `ConnectionStrings:Colmena` (y
el `AdminConnectionString` de SqlServer).

---

### 23. Login por GitHub reingresa sin pedir credenciales tras logout — comportamiento esperado de SSO
**Estado:** 🟢 No es un bug (resultado esperado de OAuth/SSO) — documentado
2026-07-23.
**Tipo:** Comportamiento de OAuth/SSO (no es defecto de implementación).
**Dónde:** flujo OAuth de GitHub ([`Program.cs`](../Program.cs), `.AddGitHub(...)`),
reportado por QA.
**Reporte de QA:** GitHub login → logout en la app → "Iniciar sesión con
GitHub" reingresa directo, sin pantalla de autorización.
**Causa:** GitHub mantiene su propia cookie de sesión en `github.com`,
independiente de la sesión de la app. Si el usuario sigue logueado en GitHub,
el proveedor aprueba la autorización sin volver a pedir credenciales — así
funciona el SSO de cualquier app de terceros. El logout de la app (que sí
limpia el token/estado local, confirmado por el front) no puede ni debe cerrar
la sesión del usuario en github.com.
**Mitigantes posibles (si producto lo pide):** GitHub OAuth **no** ofrece
`prompt=login`/`max_age` (a diferencia de OIDC/Google), así que no hay forma
limpia de forzar reautenticación desde el backend. El parámetro `login=<usuario>`
solo fuerza el selector de cuenta si el usuario tiene varias cuentas de GitHub.
Forzar logout en github.com afectaría al usuario fuera del producto (no
recomendado).
**Decisión:** se comunica a QA como resultado esperado; no se aplica cambio de
código.

---

### 24. `POST /databases/{id}/deactivate` falla siempre: `sp_DeactivateDatabase` escribe `'Inactive'`, un valor que `CK_ProvDb_Status` no permite
**Estado:** 🔴 Abierto — detectado en QA 2026-07-29 (logs del contenedor
`idempotencia-qa-back`). Requiere cambio en la BD, no en el backend.
**Tipo:** Bug funcional (desfase entre el vocabulario de estados del SP y el de
la constraint) + estado desincronizado como efecto colateral.
**Dónde:** BD `master`, tabla `dbo.ProvisionedDatabases` — constraint
`CK_ProvDb_Status` y SP `sp_DeactivateDatabase`. Se manifiesta en
[`Repository/DatabaseRepository.cs`](../Repository/DatabaseRepository.cs)
`DeactivateDatabaseAsync`, llamado desde
[`Services/DatabaseProvisioningService.cs`](../Services/DatabaseProvisioningService.cs)
`DeactivateAsync`.
**Síntoma:** todo intento de desactivar una BD devuelve `500` con
`"Ocurrió un error al procesar la solicitud."`. En los logs:

```
Microsoft.Data.SqlClient.SqlException (0x80131904): The UPDATE statement
conflicted with the CHECK constraint "CK_ProvDb_Status". The conflict occurred
in database "master", table "dbo.ProvisionedDatabases", column 'Status'.
Error Number:547
```

**Causa raíz:** los dos objetos usan vocabularios distintos para el mismo
estado. La constraint permite `('Failed','Deleted','Paused','Active','Provisioning')`
— nótese **`Paused`, no `Inactive`** — mientras que `sp_DeactivateDatabase`
ejecuta `SET Status = 'Inactive'`. El `UPDATE` viola la constraint y SQL Server
aborta con error 547. Todo el resto del sistema (C#, `docs/API.md`,
`docusaurus-docs/guia-ciclo-de-vida-bases-de-datos.md`, el frontend) habla de
`"Inactive"`: `DatabaseProvisioningService.DeactivateAsync` asigna
`detail.Status = "Inactive"` y `DeleteAsync` exige `Status == "Inactive"` antes
de permitir el borrado. El valor `'Paused'` de la constraint corresponde al
vocabulario de la propuesta de TTL del ítem 11, que **no está implementada**;
nada en el código escribe `'Paused'` hoy.
**Por qué no lo cubrió el mapper de errores:** `ApiExceptionMapper` traduce a
`400` con el mensaje real solo los `SqlException` con `Number >= 50000` (los
`THROW` intencionales de los SPs, como el `THROW 50010` que el propio
`sp_DeactivateDatabase` usa para su guarda de pertenencia). El 547 es un error
nativo del motor y cae en el `_ => 500` genérico. Es el comportamiento
correcto — no se deben exponer detalles de constraints al cliente — pero
implica que el síntoma visible para QA es un 500 opaco y el diagnóstico solo
está en los logs del contenedor.
**Efecto colateral — catálogo desincronizado:** `DeactivateAsync` revoca el
acceso físico **antes** de actualizar el catálogo. El comentario del método
justifica ese orden diciendo que así el catálogo "sigue reflejando la realidad"
si algo falla, pero eso solo aplica cuando falla el motor. Acá pasa lo
contrario: el `provisioner.DeactivateAsync` tiene éxito (login deshabilitado /
`ACCOUNT LOCK` / `NOLOGIN` / roles vaciados) y **después** falla el catálogo.
Resultado: filas que el catálogo reporta `Active` pero cuyo usuario ya no puede
conectarse. En los logs de QA se ven cuatro intentos, así que hay al menos una
BD en ese estado.
**Solución propuesta (en la BD):** alinear la constraint al vocabulario que ya
usa toda la aplicación, no al revés — cambiar el C# a `'Paused'` arrastraría el
cambio a la API pública, la documentación y el frontend.

```sql
USE master;

-- 1. Verificar si hay filas con el valor viejo antes de tocar nada.
SELECT Status, COUNT(*) AS Filas
FROM dbo.ProvisionedDatabases
GROUP BY Status;

-- 2. Reemplazar 'Paused' por 'Inactive' en el vocabulario permitido.
ALTER TABLE dbo.ProvisionedDatabases DROP CONSTRAINT CK_ProvDb_Status;

UPDATE dbo.ProvisionedDatabases
SET Status = 'Inactive'
WHERE Status = 'Paused';   -- no-op si el paso 1 devolvió 0 filas

ALTER TABLE dbo.ProvisionedDatabases
ADD CONSTRAINT CK_ProvDb_Status CHECK (
    Status IN ('Provisioning', 'Active', 'Inactive', 'Failed', 'Deleted')
);
```

**Verificar además:** que `sp_DeleteDatabase` (y cualquier otro SP que filtre
por estado, p. ej. `sp_ResetDatabasePassword` o `sp_GetUserDatabases`) no tenga
la guarda escrita contra `'Paused'`. Si `sp_DeleteDatabase` exige
`Status = 'Paused'`, el borrado quedará roto igual que la desactivación en
cuanto se aplique el fix de arriba. Revisar con `EXEC sp_helptext '<nombre>'`.
**Reparación de las filas desincronizadas:** una vez aplicado el fix, basta con
volver a llamar `POST /databases/{id}/deactivate` sobre las BDs afectadas. Los
cuatro provisioners son idempotentes en `DeactivateAsync` (`ALTER LOGIN ...
DISABLE` sobre un login ya deshabilitado, `ACCOUNT LOCK` sobre un usuario ya
bloqueado, `ALTER ROLE ... NOLOGIN` sobre un rol ya sin login, y `updateUser`
con `roles: []` sobre un usuario ya sin roles son todos no-ops exitosos), así
que reintentar es seguro y deja catálogo y motor consistentes.
**Deuda relacionada:** la columna se llama `PausedAt` pero el estado se llama
`Inactive`. Esa inconsistencia de nombres es justamente lo que originó el
desfase. Renombrar la columna implicaría tocar `ProvisionedDatabaseInfo`,
`ProvisionedDatabaseDetail` y el campo `pausedAt` de la API, así que se deja
como está y se documenta acá.
**Nota:** la tabla vive en `master` (ver ítem 22), lo que hace que este tipo de
cambio de esquema se aplique sobre una base de sistema.

---

### 25. `currentSizeMB` es un campo muerto: nadie lo escribe nunca, siempre reporta el valor de creación — CORREGIDO (pendiente de desplegar)
**Estado:** 🟡 Fix implementado 2026-07-29, **pendiente de ejecutar el script
SQL y de un `dotnet build`**. Confirmado antes por inspección de los SPs en QA.
Sustituye la duda que quedaba abierta en `docs/API.md` y en el ítem 10 ("no
está confirmado que refleje el tamaño real"): estaba confirmado que **no** lo
reflejaba, en ningún motor.
**Tipo:** Bug funcional (dato expuesto por la API que nunca corresponde a la
realidad).
**Dónde:** columna `ProvisionedDatabases.CurrentSizeMB` en `master`; expuesta
por `sp_GetDatabaseDetail` y `sp_GetUserDatabases`, mapeada en
[`Models/ProvisionedDatabaseInfo.cs`](../Models/ProvisionedDatabaseInfo.cs) y
[`Models/ProvisionedDatabaseDetail.cs`](../Models/ProvisionedDatabaseDetail.cs),
devuelta al frontend en `GET /databases` y `GET /databases/{id}`.
**Evidencia:** `sp_GetDatabaseDetail` no calcula nada, solo lee la columna:

```sql
SELECT  pd.DatabaseId, pd.UserId, pd.Engine, pd.DbName, dc.LoginName, pd.Status,
        pd.MaxStorageMB, pd.CurrentSizeMB, pd.LastActivityAt, pd.CreatedAt,
        pd.PausedAt, pd.DeletedAt
FROM ProvisionedDatabases pd
INNER JOIN DatabaseCredentials dc ...
```

Del otro lado, **ningún** SP que el backend invoca recibe un parámetro de
tamaño (`sp_ReserveDatabase`, `sp_ConfirmDatabase`, `sp_FailDatabase`,
`sp_DeactivateDatabase`, `sp_DeleteDatabase`, `sp_ResetDatabasePassword`), y no
existe ningún job ni `IHostedService` que mida las BDs físicas — el
`DatabaseQuotaMonitor` propuesto en el ítem 10 nunca se implementó. Conclusión:
el valor se fija en el `INSERT` de `sp_ReserveDatabase` y no vuelve a cambiar
jamás. Es el mismo patrón de `LastActivityAt` descrito en el ítem 11 ("no
existe ningún código que las actualice más allá de la creación inicial").
**Impacto:** el frontend muestra un indicador de almacenamiento usado que es
siempre el mismo número. Peor que no mostrarlo, porque parece un dato en vivo.
Además hace imposible que el usuario anticipe el tope: en SQL Server la cuota
**sí** se aplica de forma nativa (`MAXSIZE = {maxStorageMb}MB` en
[`SqlServerProvisioner.cs`](../Provisioners/SqlServerProvisioner.cs)), así que
el estudiante se topa con un error de espacio del motor sin que la UI le
hubiera avisado que venía llegando al límite.
**Por qué no basta con arreglar el SP:** el catálogo vive en SQL Server, y una
instancia de SQL Server puede medir sus propias BDs (`sys.master_files`,
`sp_spaceused`) pero **no** puede medir las de MySQL, PostgreSQL ni MongoDB,
que están en otros motores. Calcular en vivo dentro de
`sp_GetDatabaseDetail` daría un dato correcto solo para BDs de SQL Server y
seguiría dando `0` para los otros tres — un comportamiento inconsistente que
es peor de diagnosticar que el actual.
**Solución propuesta (converge con el ítem 10):** la medición tiene que venir
del backend, que sí habla los cuatro protocolos. Es exactamente la misma
medición que necesita el enforcement de cuota del ítem 10, así que conviene
implementarlas juntas:

1. Agregar `Task<decimal> GetSizeMbAsync(string dbName, CancellationToken ct)`
   a `IDatabaseProvisioner`, con una implementación por motor:
   - MySQL: `SELECT SUM(data_length + index_length) FROM information_schema.tables WHERE table_schema = @dbName`
   - PostgreSQL: `SELECT pg_database_size(@dbName)`
   - SQL Server: `SELECT SUM(size) * 8.0 / 1024 FROM sys.master_files WHERE database_id = DB_ID(@dbName)`
   - Mongo: `db.runCommand({ dbStats: 1 })` → `dataSize`
2. Nueva SP de catálogo `sp_UpdateDatabaseSize(@DatabaseId, @CurrentSizeMB)` y
   su método en `IDatabaseRepository`, mismo patrón que las existentes.
3. Un `IHostedService` (`DatabaseQuotaMonitor`, el del ítem 10) que recorra
   periódicamente las BDs `Active`, llame a `GetSizeMbAsync` y persista el
   resultado. El mismo recorrido decide si hay que revocar escrituras por
   exceder cuota, que es lo que pide el ítem 10.

**Alternativa táctica descartada:** medir bajo demanda solo en
`GET /databases/{id}`. Daría un dato real en el detalle sin job ni SPs nuevas,
pero agrega una conexión al motor por cada request (latencia, y el endpoint
pasa a fallar si el motor está caído) y no arregla `GET /databases`, donde
habría que medir N bases por request. Se descartó a favor del job.

**Solución aplicada (2026-07-29) — decisión: solo medición, sin enforcement.**
Se implementó el camino completo de sincronización. El enforcement de cuota
(revocar escrituras al pasarse) queda fuera de alcance y sigue en el ítem 10,
que ahora solo necesita actuar sobre un dato que ya es real.

*Medición, una por motor* — `GetSizeMbAsync` agregado a
[`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs) y
a los cuatro provisioners. **En MySQL, PostgreSQL y Mongo no se creó ningún
objeto**: el backend solo les lanza una consulta con la conexión admin que ya
tenía. Cada motor reporta una noción distinta de "tamaño" y se eligió a
propósito cuál usar:

| Motor | Consulta | Qué mide y por qué esa |
|---|---|---|
| SQL Server | `SUM(size) * 8 / 1024` sobre `sys.master_files` | Espacio **asignado** a los archivos. Es contra lo que el motor aplica `MAXSIZE`, así que es lo que le importa al estudiante para saber cuánto le queda. |
| MySQL | `SUM(data_length + index_length)` en `information_schema.tables` | Datos + índices. Es una **estimación** de InnoDB, no se actualiza en tiempo real; puede quedar algo por debajo justo tras una carga grande. |
| PostgreSQL | `pg_database_size(@DbName)` | Tamaño real en disco de la BD completa. El más fiel de los cuatro: no es estimación. |
| Mongo | `dbStats` → `storageSize + indexSize` | Espacio en disco ya comprimido, más índices. Se prefirió sobre `dataSize` (bytes lógicos sin comprimir) porque es lo comparable con los otros tres. |

Los cuatro devuelven `0` en vez de lanzar si la BD ya no existe en el motor:
para un job, una base desaparecida no es un error que deba abortar el ciclo.

*Catálogo* — dos SPs nuevos en
[`sql/2026-07-29-size-sync.sql`](../sql/2026-07-29-size-sync.sql), idempotente
(`CREATE OR ALTER`): `sp_GetDatabasesForSizeSync` (todas las BDs `Active` de
todos los usuarios; los SPs existentes no servían porque filtran por
`@UserId` y el job no actúa en nombre de nadie — devuelve las mismas columnas
que `sp_GetUserDatabases` para reusar el tipo `ProvisionedDatabaseInfo`) y
`sp_UpdateDatabaseSize(@DatabaseId, @CurrentSizeMB)`. Este último **no** toca
`LastActivityAt` a propósito: esa columna representa actividad del estudiante
(ítem 11), y escribirla desde un job haría que ninguna BD pareciera nunca
inactiva, rompiendo el futuro job de TTL antes de existir.

*Job* — [`Services/DatabaseSizeMonitor.cs`](../Services/DatabaseSizeMonitor.cs),
un `BackgroundService` que cada N minutos recorre las BDs activas, mide y
persiste **solo lo que cambió** (evita escrituras inútiles y deja los logs de
EF legibles). Es de mejor esfuerzo por diseño: timeout por base, todas las
excepciones atrapadas y registradas por base, y nunca deja escapar una
excepción — un `BackgroundService` que lo hace tumba el host completo. Abre su
propio scope de DI en cada ciclo porque es Singleton y el repositorio y el
factory son Scoped.

*Configuración* — [`Services/SizeMonitorSettings.cs`](../Services/SizeMonitorSettings.cs),
sección `Provisioning:SizeMonitor`. **No hace falta configurarla**: todos los
valores tienen default y `appsettings.json` no está versionado. Si se quiere
ajustar:

```jsonc
"Provisioning": {
  "SizeMonitor": {
    "Enabled": true,               // false para apagarlo (útil en local, donde
                                   // no están los 4 motores levantados)
    "IntervalMinutes": 15,
    "StartupDelaySeconds": 30,     // margen para que los motores estén listos
    "PerDatabaseTimeoutSeconds": 30
  }
}
```

**Pendiente antes de dar esto por cerrado:**
1. Ejecutar `sql/2026-07-29-size-sync.sql` en la instancia del catálogo. Sin
   esto el job falla en cada ciclo con "no existe el procedimiento" — queda en
   los logs y no tumba nada, pero no sincroniza.
2. `dotnet build` — no se pudo compilar en el entorno donde se escribió el fix
   (sin SDK de .NET). Cambios verificados por lectura.
3. Desplegar y esperar un ciclo; después verificar con la consulta que trae el
   script al final. Si `CurrentSizeMB` sigue en `0.00` en todas las filas,
   revisar `Enabled` y buscar "Sincronización de tamaños" en los logs.

---

### 26. Desactivar una BD era un camino sin retorno: faltaba el endpoint de reactivar — IMPLEMENTADO
**Estado:** 🟡 Fix implementado 2026-07-29, **pendiente de ejecutar el SP y de
desplegar**. Cierra el punto 7 del backlog de `docs/claude.md`.
**Tipo:** Funcionalidad faltante (no era una limitación técnica).
**Dónde:** [`Controllers/DatabasesController.cs`](../Controllers/DatabasesController.cs),
[`Services/DatabaseProvisioningService.cs`](../Services/DatabaseProvisioningService.cs),
los cuatro provisioners, y el SP nuevo en
[`sql/2026-07-29-reactivate.sql`](../sql/2026-07-29-reactivate.sql).
**Problema:** `POST /databases/{id}/deactivate` revoca el acceso pero **nunca
borra los datos** — la BD física y su contenido siguen intactos, solo el
login queda deshabilitado. Aun así, la única transición disponible desde
`Inactive` era `DELETE`, que sí es irreversible. En la práctica un estudiante
que desactivaba por error perdía su base: la UI solo le ofrecía borrarla. La
documentación llegó a recomendar tratarlo como "una acción destructiva de
primer nivel", que era la consecuencia de la carencia, no una decisión de
diseño.
**Solución aplicada:** `POST /databases/{id}/reactivate`, la inversa exacta de
desactivar. Cada provisioner deshace lo suyo — `ALTER LOGIN ... ENABLE` en SQL
Server, `ACCOUNT UNLOCK` en MySQL, `ALTER ROLE ... LOGIN` en PostgreSQL, y en
Mongo se restituye el rol `readWrite` sobre la propia BD. El SP nuevo
`sp_ReactivateDatabase` pasa el estado de `Inactive` a `Active`, pone
`PausedAt` en `NULL` y **sí** refresca `LastActivityAt` (a diferencia de
`sp_UpdateDatabaseSize`, que a propósito no la toca: medir el tamaño lo hace un
job, pero reactivar es una acción explícita del estudiante; sin esto el futuro
job de TTL del ítem 11 volvería a pausar una BD recién reactivada en su
siguiente pasada).

**Nota sobre Mongo:** el comentario de `DeactivateAsync` advertía que para
revertir "habría que recordar el rol/BD original". No hizo falta persistirlo:
`CreateAsync` siempre otorga exactamente el mismo rol (`readWrite` scoped a la
BD del estudiante, nunca nada más amplio), así que `ReactivateAsync` lo
reconstruye. Queda anotado en el código que si algún día `CreateAsync` empieza
a otorgar roles variables, esa suposición deja de valer y el rol tendrá que
guardarse en el catálogo.

**Orden de operaciones — motor primero, catálogo después.** Es el mismo orden
que `DeactivateAsync` pero por una razón distinta, y conviene no copiar el
razonamiento de allá sin pensarlo. Al desactivar, ese orden evita que el
catálogo diga `Inactive` mientras el usuario todavía puede conectarse. Al
reactivar, evita el desbalance contrario: si el catálogo dijera `Active` y el
motor hubiera fallado, el usuario vería su BD como disponible sin poder
conectarse, y **la UI ni siquiera le ofrecería el botón de reactivar** para
reintentar, porque la guarda exige `Inactive` — quedaría atascado sin salida.
Con el orden elegido, un fallo del catálogo deja una BD físicamente habilitada
pero marcada `Inactive`: el botón sigue visible, el usuario lo vuelve a pulsar
y, como la operación es idempotente en los cuatro motores, el reintento
termina limpio. De los dos estados desincronizados posibles, se eligió el
recuperable. Es la misma lección del ítem 24.

**Pendiente:** ejecutar `sql/2026-07-29-reactivate.sql` (después del script del
ítem 24 — no se puede reactivar algo que nunca logró llegar a `Inactive`),
compilar y desplegar.

---

### 27. El `host` entregado al usuario salía del host interno del motor: en despliegue devolvía el nombre del contenedor, y sin la clave caía en `localhost` en silencio — CORREGIDO
**Estado:** 🟡 Fix implementado (pendiente configurar `Provisioning:IpVps`, compilar y desplegar).
**Tipo:** Configuración / funcionalidad (dato inservible expuesto en la API).
**Dónde:** los cuatro provisioners —
[`Provisioners/PostgresProvisioner.cs`](../Provisioners/PostgresProvisioner.cs),
[`MySqlProvisioner.cs`](../Provisioners/MySqlProvisioner.cs),
[`SqlServerProvisioner.cs`](../Provisioners/SqlServerProvisioner.cs),
[`MongoProvisioner.cs`](../Provisioners/MongoProvisioner.cs) — consumido desde
[`Services/DatabaseProvisioningService.cs`](../Services/DatabaseProvisioningService.cs)
(`CreateDatabaseResponse.Host` y `MapToDetailResponse`).

**Problema:** cada provisioner leía `Provisioning:{Engine}:Host` y lo devolvía
tal cual en el campo `host` de `POST /databases`, `GET /databases`,
`GET /databases/{id}` y del correo de credenciales. Esa clave describía "cómo
llega el backend al motor", que **en despliegue con Docker es el nombre del
contenedor** (`colmena-postgres`, `colmena-mysql`, …): un nombre que solo
resuelve dentro de la red interna de Docker. El usuario recibía credenciales
correctas con un host al que no puede conectarse desde su máquina.

Dos agravantes:

- **Fallback silencioso:** `?? "localhost"`. Si la clave no estaba, la API
  reportaba `localhost` sin warning ni error de arranque — el fallo aparecía
  recién en el cliente del usuario, como un timeout de conexión sin
  explicación. Contrastaba con `AdminConnectionString`, que sí lanza si falta.
- **Dos conceptos en una sola clave:** la dirección interna (backend → motor) y
  la pública (usuario → motor) no tienen por qué coincidir, y en despliegue
  nunca coinciden.

**Solución aplicada** (decisión de esta sesión): separar los dos conceptos.

- Nueva clave global `Provisioning:IpVps` — la IP pública del VPS (o el dominio
  que apunte a él), enlazada en
  [`Services/ProvisioningSettings.cs`](../Services/ProvisioningSettings.cs). Es
  **una sola** para los cuatro motores porque todos corren en la misma máquina;
  lo que sigue siendo por motor es `Provisioning:{Engine}:Port`, el puerto
  publicado.
- `Provisioning:{Engine}:Host` **deja de leerse** (se puede borrar del
  `appsettings.json`). El host interno sigue viviendo donde corresponde: dentro
  de cada `AdminConnectionString`.
- Los provisioners reciben `IOptions<ProvisioningSettings>` y exponen
  `Host => IpVps`.
- [`Program.cs`](../Program.cs) **valida al arrancar** que `Provisioning:IpVps`
  esté configurada y lanza `InvalidOperationException` con un mensaje explícito
  si falta — mismo criterio que `Cors:AllowedOrigins`. Se eligió fallar de una
  en vez de warning + `localhost`: un despliegue mal configurado que arranca
  "bien" y entrega datos inservibles es exactamente el modo de falla que este
  ítem describe.

**Efecto secundario a tener en cuenta:** el host nunca se persistió en el
catálogo (`ProvisionedDatabaseDetail` no lo tiene; `MapToDetailResponse` lo toma
del provisioner en cada consulta). Cambiar `Provisioning:IpVps` cambia entonces
retroactivamente el host reportado de **todas** las BDs ya creadas — deseable si
el VPS cambia de IP, pero implica que el valor histórico no queda registrado en
ninguna parte.

**Aplicado en configuración (2026-07-30):** el `appsettings.json` local ya tiene
`Provisioning:IpVps: "100.99.206.50"` y **se borraron las cuatro claves
`Provisioning:{Engine}:Host`**, que contenían exactamente los nombres de
contenedor que describe este ítem (`idempotencia-sqlserver`,
`idempotencia-postgres`, `idempotencia-mysql`, `idempotencia-mongodb`). Los
`AdminConnectionString` siguen usando esos nombres, que es su lugar correcto.

**Pendiente:** compilar, desplegar, y confirmar en el ambiente desplegado que la
IP configurada sea alcanzable **desde la máquina del usuario** — `100.99.206.50`
está en el rango CGNAT (100.64.0.0/10, típico de Tailscale): sirve si los
usuarios entran por esa misma red privada, no desde internet abierta. Si el
acceso es público, ahí va la IP pública del VPS o un dominio.

---

### 28. Conectarse a la BD MySQL recién creada exigía un paso manual (`allowPublicKeyRetrieval`) y dejaba las credenciales sin cifrar — CORREGIDO
**Estado:** 🟡 Fix implementado (pendiente compilar, desplegar y ejecutar el backfill de usuarios existentes).
**Tipo:** Usabilidad + seguridad (credenciales en tránsito).
**Dónde:** [`Provisioners/MySqlProvisioner.cs`](../Provisioners/MySqlProvisioner.cs)
(y los otros tres provisioners para las cadenas de conexión),
`Provisioning:MySql:AdminConnectionString` en `appsettings.json`,
[`Services/EmailTemplates.cs`](../Services/EmailTemplates.cs),
[`DTOs/DatabaseDtos.cs`](../DTOs/DatabaseDtos.cs) y
[`DTOs/AuthDtos.cs`](../DTOs/AuthDtos.cs).

**Problema reportado:** un usuario nuevo con su BD MySQL recién aprovisionada no
podía conectarse desde su gestor (DBeaver y similares) sin activar a mano la
opción `allowPublicKeyRetrieval`.

**Causa:** desde MySQL 8 el plugin de autenticación por defecto es
`caching_sha2_password`. Cuando la conexión **no está cifrada**, ese plugin
necesita un intercambio de clave pública RSA para no mandar la contraseña en
claro; el cliente no tiene esa clave, así que falla y sugiere
`allowPublicKeyRetrieval=true` — que además es peor: le pide al cliente aceptar
una clave pública sin verificar, lo que habilita un MITM que se quede con la
contraseña en texto plano (la propia doc del driver lo advierte, y por eso viene
desactivado). Con la conexión cifrada el problema desaparece: el intercambio
ocurre dentro del canal TLS.

Nada en el backend estaba forzando ese cifrado:

- El `AdminConnectionString` de MySQL no traía ninguna opción de SSL, así que
  MySqlConnector usaba su default `SslMode=Preferred` — "cifra si el servidor
  puede", sin garantía ni aviso si no.
- El backend **no le entregaba ninguna cadena de conexión al usuario**: la API y
  el correo daban host, puerto, usuario y contraseña por separado, así que cada
  usuario armaba la conexión en su cliente y ahí decidía (o no) el cifrado.
- Los usuarios se creaban sin `REQUIRE SSL`, con lo cual el motor aceptaba
  conexiones sin cifrar. Con el puerto expuesto públicamente, eso significa
  credenciales de BD viajando en texto plano por internet.

> **Nota sobre el diagnóstico inicial:** se planteó como "reemplazar
> `useSSL=true` por `sslMode=REQUIRED`". `useSSL` es un parámetro de
> **Connector/J**, el driver Java (el que usa DBeaver), donde efectivamente está
> deprecado. Este backend usa **MySqlConnector** (.NET), donde la opción se llama
> `SslMode` y `useSSL` no existiría; y de hecho `useSSL` no aparecía en ningún
> archivo del repo. El fondo del diagnóstico igual era correcto: faltaba forzar
> el cifrado. La diferencia práctica es que no había un parámetro que corregir,
> había uno que agregar en dos lugares distintos.

**Solución aplicada:**

1. **Conexión propia del backend:** `SslMode=Required` en
   `Provisioning:MySql:AdminConnectionString`. En MySqlConnector, `Required`
   cifra pero **no** valida el certificado (solo `VerifyCA`/`VerifyFull` lo
   validan), que es exactamente lo que hace falta con un certificado
   autofirmado.
2. **Cadenas de conexión para el usuario (nuevas):** `BuildClientConnection` en
   [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs)
   y los cuatro provisioners, devolviendo
   [`Models/ClientConnectionInfo.cs`](../Models/ClientConnectionInfo.cs) →
   campos `connectionUri` (formato nativo, con credenciales) y `jdbcUrl` (para
   clientes Java, sin credenciales) en `POST /databases`, en el
   `mySqlDatabase` del login y en los **dos correos** de credenciales. Cada
   motor escribe el parámetro de cifrado a su manera
   (`ssl-mode=REQUIRED` / `sslMode=REQUIRED` / `sslmode=require` /
   `tls=true` / `Encrypt=True`), que es justo lo que no se le puede pedir al
   usuario que adivine. Usuario y contraseña van percent-encoded: el alfabeto de
   `PasswordGenerator` incluye `#$%&*+-`, que romperían la URI en crudo.
3. **`CREATE USER ... REQUIRE SSL`** en MySQL: el motor **rechaza** conexiones
   sin cifrar de los usuarios aprovisionados. Es la única mitad que el cliente
   no puede eludir — el `ssl-mode` de la cadena es un pedido, no una garantía.
4. **Flag `Provisioning:{Engine}:RequireTls`** para gobernar los puntos 2 y 3.
   Está en `true` para MySQL (TLS confirmado funcionando) y SqlServer (cifra
   siempre, y el admin ya usaba `Encrypt=True`), y en `false` para Postgres y
   Mongo: sus imágenes oficiales no habilitan TLS por defecto, y exigirlo contra
   un motor sin certificado **dejaría a esos usuarios sin poder conectarse**.
   Cuando esos contenedores tengan certificado, se prende el flag y no hace
   falta tocar código.

**Pendiente:**

- Compilar y desplegar.
- Ejecutar [`sql/2026-07-30-mysql-require-ssl.sql`](../sql/2026-07-30-mysql-require-ssl.sql)
  **en el motor MySQL** (no en el catálogo de SQL Server) para aplicar
  `REQUIRE SSL` a los usuarios creados antes de este cambio — los nuevos ya
  salen así. El script verifica primero, genera los `ALTER` para revisarlos y
  documenta cómo revertir.
- Habilitar TLS en los contenedores de Postgres y Mongo y prender su
  `RequireTls`. Mientras siga en `false`, esas credenciales viajan sin cifrar
  por el puerto público: **el punto de seguridad queda cerrado solo para MySQL y
  SqlServer**.

---

### 29. Todos los secretos del backend quedan horneados dentro de la imagen Docker
**Estado:** 🔴 Abierto (decisión pendiente, ver más abajo).
**Tipo:** Seguridad (exposición de credenciales).
**Dónde:** [`Dockerfile`](../Dockerfile) (`COPY . ./`) + [`.dockerignore`](../.dockerignore).
**Problema:** `.dockerignore` excluye `bin/`, `obj/`, `.git/` y los archivos de
IDE, pero **no `appsettings.json`**. Como el Dockerfile hace `COPY . ./` antes
de `dotnet publish`, ese archivo entra en la etapa de build y su copia publicada
queda en la imagen final. Hoy eso significa que cualquiera que pueda hacer `pull`
de la imagen —o `docker run ... cat appsettings.json`, sin siquiera arrancar la
app— lee en texto plano:

- la contraseña de `sa` del servidor del catálogo,
- las credenciales de `idempotencia_login` en el servidor de Raft Consensus,
- las contraseñas de root de MySQL y Postgres, y la de admin de Mongo,
- el token de Cloudflare (permite crear/borrar registros DNS de la zona),
- la API key de la célula socia y la del servicio de Mongo,
- la App Password de Gmail,
- la clave de firma de los JWT (permite fabricar tokens de cualquier usuario),
- los `ClientSecret` de Google y GitHub.

`appsettings.json` está en `.gitignore`, así que **no** viaja al repositorio: la
fuga es específica de la imagen. Es la razón por la que el archivo estar ignorado
en git da una falsa sensación de que los secretos están contenidos.

**Por qué no se corrigió de una:** agregar `appsettings.json` al `.dockerignore`
es una línea, pero **rompería el despliegue actual**. El backend no lee variables
de entorno para estos valores hoy: toda la configuración vive en ese archivo, así
que un contenedor sin él ni siquiera arranca (`Program.cs` falla al validar
`Provisioning:IpVps`). El fix real es mover los secretos fuera del archivo, y esa
decisión la tomó el usuario en la sesión 21: **por ahora se quedan en
`appsettings.json`**.

**Solución propuesta (cuando se decida abordarlo):**
1. Agregar `appsettings.json` a `.dockerignore`.
2. Pasar los secretos por variables de entorno con el separador de doble guion
   bajo de .NET, que las mapea sobre las mismas claves sin tocar código:
   `ConnectionStrings__Colmena`, `Jwt__Key`, `Dns__ApiToken`,
   `Email__Password`, `Authentication__Google__ClientSecret`,
   `Authentication__GitHub__ClientSecret`,
   `Provisioning__SqlServer__AdminConnectionString`,
   `Provisioning__Postgres__AdminConnectionString`,
   `Provisioning__MySql__AdminConnectionString`,
   `Provisioning__MySql__Remote__ApiKey`,
   `Provisioning__Mongo__AdminConnectionString`,
   `Provisioning__Mongo__Remote__ApiKey`.
3. Dejar en `appsettings.json` solo lo no sensible (puertos, hosts públicos,
   flags, URLs base) y versionarlo como plantilla.
4. En desarrollo local, User Secrets (`dotnet user-secrets`) o
   `appsettings.Development.json`, que ya está en `.gitignore`.

**Mitigación mientras tanto:** tratar la imagen como si fuera el archivo de
secretos — registro privado, sin publicarla, y rotar todo lo de arriba si alguna
vez estuvo en un registro accesible.

---

### 30. `CREATE DATABASE` de SQL Server fallaba con "File option FILENAME is required" en el servidor del proveedor — CORREGIDO
**Estado:** 🟡 Fix implementado (pendiente confirmar en vivo).
**Tipo:** Bug funcional (aprovisionamiento roto para un motor completo).
**Dónde:** [`Provisioners/SqlServerProvisioner.cs`](../Provisioners/SqlServerProvisioner.cs) — `CreateAsync`, paso 1.
**Problema:** el provisioner creaba la BD con la cuota en la misma sentencia:

```sql
CREATE DATABASE [x] ON PRIMARY (NAME = 'x_data', SIZE = 8MB, MAXSIZE = 20MB, FILEGROWTH = 4MB);
```

En cuanto se escribe un `<filespec>` —el paréntesis con `NAME`/`SIZE`/etc.— SQL
Server exige también `FILENAME`, la ruta física del archivo, y si falta responde
con el error 1036. El resultado: **ninguna base de SQL Server se podía crear**,
y el fallo aparecía recién al intentarlo contra la instancia de Raft Consensus
(sesión 20), porque hasta entonces ese motor no se había ejercitado en serio.

No es un problema exclusivo del servidor del proveedor: la sentencia habría
fallado igual en el servidor propio. Lo que cambió fue que empezó a usarse.

**Por qué no se puede simplemente agregar FILENAME:** haría falta la ruta del
directorio de datos del motor, que vive en la infraestructura del proveedor y
puede cambiar sin avisarnos. Escribirla en el código sería acoplarnos a un
detalle que no controlamos.

**Solución aplicada:** partir en dos statements — un `CREATE DATABASE [x];`
pelado, que deja al servidor elegir dónde poner los archivos, y después un
`ALTER DATABASE [x] MODIFY FILE (NAME = 'x', MAXSIZE = ..., FILEGROWTH = 4MB)`
que aplica la cuota. El nombre lógico del archivo de datos tras un
`CREATE DATABASE` sin filespec es siempre el nombre de la base.

Se dejó de fijar `SIZE` deliberadamente: `MODIFY FILE` no puede reducir el
tamaño actual, así que pedir un valor igual o menor al heredado de `model`
haría fallar la creación entera por un dato que no aportaba nada — el tamaño
inicial lo define `model` y la cuota la impone `MAXSIZE`.

**Nota sobre el log:** solo se acota el archivo de datos. El de transacciones
(`<base>_log`) se deja crecer: limitarlo puede dejar la BD en solo lectura por
una transacción larga, que es un modo de falla peor que el espacio que ocupa.

---

### 31. El proveedor bloquea `DROP DATABASE` por SQL: no podemos borrar bases de SQL Server
**Estado:** 🔵 Conocido / mitigado en código; la purga física queda manual.
**Tipo:** Restricción del entorno (no es un bug nuestro) + su efecto colateral.
**Dónde:** [`Provisioners/SqlServerProvisioner.cs`](../Provisioners/SqlServerProvisioner.cs) — `DropAsync` y `CreateAsync`.
**Problema:** la instancia de Raft Consensus tiene un **trigger DDL** que cancela
cualquier `DROP DATABASE` ejecutado por SQL:

```
Operación cancelada: No tienes permisos para borrar la base de datos por SQL.
Por favor utiliza el panel de Raft.
The transaction ended in the trigger. The batch has been aborted.
```

No aparece en ninguno de los dos documentos que entregó el proveedor
(`credencialesidempotencia.pdf` dice explícitamente que el login puede "gestionar
completamente su propia base de datos"). Se descubrió en la primera prueba real
de creación, sesión 21.

Rompe dos supuestos del provisioner:

1. **La reversión de una creación fallida no puede limpiar.** La BD queda viva y
   el catálogo sin registro.
2. **`DELETE /databases/{id}` no puede cumplir su contrato** ("borrado físico
   real, irreversible").

Y tiene un efecto en cascada: como `sp_ReserveDatabase` genera el mismo nombre
para el mismo (usuario, etiqueta), **todo reintento choca con
`Database ... already exists` (error 1801) para siempre**, hasta que alguien
purgue la base a mano.

**Decisiones tomadas (confirmadas con el usuario):**

- `DropAsync` **revoca el acceso primero** (`DROP LOGIN`, que sí está permitido)
  y solo después intenta el borrado físico. Si el trigger lo cancela, se
  registra un ERROR con el nombre exacto a purgar y **no se propaga**: el
  catálogo marca la base como eliminada y el endpoint sigue devolviendo 204. Sin
  login nadie puede conectarse, así que los datos quedan inaccesibles aunque el
  archivo siga ocupando espacio.
- Se restaura `MULTI_USER` si el borrado falla. El `SET SINGLE_USER WITH ROLLBACK
  IMMEDIATE` previo corre en su propia transacción implícita y el rollback del
  trigger NO lo deshace: sin esa restauración la base quedaría aceptando una sola
  conexión, peor que el estado inicial.
- La colisión de nombres devuelve **409 con un mensaje accionable** (purgar desde
  el panel o elegir otro nombre) en vez del "ya existe" crudo. Se descartó
  agregar un sufijo automático: ocultaría el problema y el servidor acumularía
  bases huérfanas en silencio.

**Pendiente:** pedirle a Raft que exceptúe a `idempotencia_login` del trigger, o
que exponga un endpoint de borrado. Mientras tanto, cada base de SQL Server
eliminada desde Colmena deja un archivo que alguien tiene que purgar desde su
panel — conviene revisar el log periódicamente buscando "No se pudo borrar
físicamente la BD". Contacto: consensusraft@gmail.com.

---

## Resumen por severidad

> Convención de estado: 🔴 Abierto · 🟡 Fix entregado, sin confirmar · 🟢
> Resuelto (confirmado) · 🔵 Resuelto parcialmente · 🟠 Reabierto a propósito
> (riesgo conocido y aceptado por decisión de producto).

| # | Hallazgo | Severidad | Estado |
|---|---|---|---|
| 9 | `sp_GetLoginByEmail` referencia tabla `Roles` inexistente (rompe login) — confirmado en vivo | 🔴 Alta | 🟢 Resuelto |
| 13 | Usuarios aprovisionados veían/se conectaban a BDs de otros usuarios | 🔴 Alta | 🔵 Parcial (SQL Server/Postgres/Mongo 🟢, MySQL 🔴) |
| 14 | Enumeración de cuentas OAuth-only en `POST /auth/login` (mensaje distinto sin necesitar password) | 🔴 Alta | 🟠 Reabierto a propósito (2026-07-29): mensajes específicos por decisión de producto; mitigado solo por rate limit `auth` |
| 16 | BD MySQL de usuarios OAuth quedaba con contraseña imposible de entregar (huérfana) | 🔴 Alta | 🟢 Resuelto |
| 11 | Ciclo de vida (TTL) sin implementar: no hay pausado/eliminación automática por inactividad | 🔴 Alta | 🔴 Abierto |
| 1 | Token JWT en query string del redirect OAuth | 🔴 Alta | 🔴 Abierto |
| 15 | Redirect OAuth también expone email/nombre/rol/userId en query string (amplía ítem 1) | 🔴 Alta | 🔴 Abierto |
| 3 | Secretos en texto plano en `appsettings.json` | 🔴 Alta (higiene) | 🔴 Abierto |
| 10 | Cuota de almacenamiento no se hace cumplir en Postgres/MySQL/Mongo (solo SQL Server) | 🟠 Media | 🔴 Abierto |
| 12 | Límite de conexiones concurrentes sin equivalente nativo en SQL Server/Mongo | 🟠 Media | 🔵 Parcial (MySQL/Postgres 🟢) |
| 4 | Vulnerabilidad conocida en `Microsoft.OpenApi` | 🟠 Media | 🟡 Pin a 2.7.5 aplicado; pendiente confirmar con build |
| 2 | Sin rate limit dedicado en endpoints OAuth | 🟠 Media | 🟢 Resuelto (política `oauth` 20/min) |
| 2b | Auto-actualización continua de la documentación | Proceso | 🔵 Parcial (convención + scheduled task) |
| 5 | `CurrentSizeMB` sin tipo de columna (truncamiento silencioso) | 🟡 Baja-Media | 🟢 Resuelto |
| 7 | Claim `"UserId"` duplicado como string literal | 🟡 Baja | 🟢 Resuelto |
| 6 | `idempotencia.http` con endpoint obsoleto | ⚪ Cosmético | 🟢 Resuelto |
| 8 | Doc desactualizada sobre callback OAuth | ⚪ Ya corregido en esta revisión | 🟢 Resuelto |
| 17 | `redirect_uri` de OAuth en `http://` en vez de `https://` detrás del reverse proxy (QA) — rompía el login completo | 🔴 Alta | 🟢 Resuelto (confirmado en QA) |
| 18 | Validación de entrada débil en Email/FullName/DbName/Engine — endurecida | 🟡 Media (defensa en profundidad) | 🟢 Resuelto |
| 19 | Ciclo de vida manual de BD (detalle/desactivar/eliminar/reset password) — feature nueva | Feature | 🟢 Resuelto (SPs desplegados + SMTP + confirmado en vivo) |
| 20 | `POST /auth/register` rechazaba un correo de 150 caracteres (máximo documentado) por validadores apilados | 🟡 Media (falso negativo, bloqueaba registros válidos) | 🟢 Resuelto |
| 21 | `POST /auth/register` aceptaba contraseñas de más de 12 caracteres (límite real de negocio) | 🟡 Media (falso positivo, dato inválido aceptado) | 🟢 Resuelto |
| 22 | Connection string apunta a `Database=master` (funciona; mala práctica) — reportado por el front | 🟡 Config | 🔵 Conocido/aceptado (dejar como está) |
| 23 | GitHub reingresa sin pedir credenciales tras logout | ⚪ N/A | 🟢 No es bug (SSO esperado) |
| 24 | `POST /databases/{id}/deactivate` siempre falla: `sp_DeactivateDatabase` escribe `'Inactive'` y `CK_ProvDb_Status` solo permite `'Paused'` (error 547) | 🔴 Alta (funcionalidad rota + catálogo desincronizado) | 🔴 Abierto (fix es un `ALTER TABLE`, ver ítem 24) |
| 25 | `currentSizeMB` nunca se actualizaba: ningún SP lo escribía y no había job que midiera | 🟠 Media (dato falso expuesto en la API, en los 4 motores) | 🟡 Fix implementado (`DatabaseSizeMonitor` + 2 SPs); falta ejecutar el script SQL, compilar y desplegar |
| 26 | Desactivar era un camino sin retorno: faltaba `POST /databases/{id}/reactivate` pese a que los datos nunca se borran | 🟠 Media (pérdida de acceso evitable por un clic del usuario) | 🟡 Implementado (`sp_ReactivateDatabase` + endpoint); falta ejecutar el script SQL, compilar y desplegar |
| 27 | El `host` entregado al usuario salía de `Provisioning:{Engine}:Host` (en Docker, el nombre del contenedor) y caía a `localhost` en silencio si faltaba | 🔴 Alta (credenciales inservibles desde fuera del servidor, sin señal de error) | 🟢 Resuelto en código + config (`Provisioning:IpVps` + validación al arrancar); falta compilar y desplegar |
| 28 | Conectarse a MySQL exigía activar `allowPublicKeyRetrieval` a mano, y las credenciales podían viajar sin cifrar por el puerto público | 🔴 Alta (seguridad) + 🟠 Media (usabilidad) | 🟡 Implementado (`SslMode=Required`, `REQUIRE SSL`, `connectionUri`/`jdbcUrl`); falta compilar, desplegar y correr el backfill. Postgres/Mongo siguen sin TLS |
| 29 | Todos los secretos (JWT, `sa`, Cloudflare, Gmail, las 2 API keys, OAuth) quedan dentro de la imagen Docker: `.dockerignore` no excluye `appsettings.json` | 🔴 Alta (seguridad) | 🔴 Abierto — el fix rompe el despliegue actual hasta que los secretos salgan del archivo (decisión: se quedan por ahora, sesión 21) |
| 30 | `CREATE DATABASE ... ON PRIMARY (...)` sin `FILENAME` → error 1036: ninguna base de SQL Server se podía crear | 🔴 Alta (motor completo inutilizable) | 🟡 Corregido (CREATE + ALTER MODIFY FILE); falta confirmar en vivo |
| 31 | Raft bloquea `DROP DATABASE` por SQL con un trigger DDL: no se pueden borrar bases de SQL Server, y los reintentos chocan con el nombre para siempre | 🟠 Media (restricción externa; el acceso sí se revoca) | 🔵 Mitigado en código; falta negociar la excepción con el proveedor |
