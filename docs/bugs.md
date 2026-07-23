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
**Estado:** 🔴 Abierto.
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

---

### 4. Dependencia `Microsoft.OpenApi` con vulnerabilidad conocida (alta severidad)
**Estado:** 🔴 Abierto.
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
**Estado:** 🔴 Abierto.
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
**Estado:** 🔴 Abierto.
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
**Estado:** 🔴 Abierto.
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

### 14. Enumeración de cuentas: `POST /auth/login` distinguía cuentas OAuth-only sin necesitar la contraseña — CORREGIDO
**Estado:** 🟢 Resuelto.
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
**Solución aplicada:** se quitó la llamada a `EnsureMySqlDatabaseAsync` de
`ExternalLoginAsync`. Los usuarios que entran por OAuth ya NO obtienen su BD
MySQL automáticamente en el primer login.
**Lo que el frontend debe hacer en su lugar:** llamar `POST /databases`
(`{"engine": "MySql", "dbName": "principal"}`) explícitamente apenas aterriza
en `/oauth/callback`, idealmente solo si `GET /databases` viene vacío (mismo
chequeo de "primera vez" que ya hace el backend para login por contraseña).
Esa respuesta sí es JSON normal y sí entrega las credenciales de forma segura.
Ver `docs/API.md` sección 3.1 y 4.3, actualizadas con esta nota.

---

### 17. `redirect_uri` de OAuth viajaba como `http://` en vez de `https://` detrás del reverse proxy de QA — CORREGIDO
**Estado:** 🟡 Fix entregado (aplicado en código; pendiente que el usuario
confirme en vivo tras desplegar en QA).
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
**Estado:** 🟡 Fix entregado (código completo; requiere 2 pasos manuales antes
de funcionar — ver "Pendiente" abajo).
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
**Pendiente para que funcione en un ambiente real (2 pasos manuales, ninguno
autoejecutable por el backend):**
1. Correr [`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql)
   contra la base real. **Asume que `ProvisionedDatabases` ya tiene una
   columna `LoginName`** (el script trae instrucciones si no existe).
2. Configurar la sección `Email` de `appsettings.json` con una cuenta SMTP
   real (hoy tiene placeholders) — para Gmail/Workspace se necesita una
   **App Password** (requiere verificación en 2 pasos activada en la
   cuenta), no la contraseña normal. Recomendado moverlo a User Secrets /
   variables de entorno en vez de dejarlo en texto plano (mismo criterio que
   el ítem 3 de este documento).
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

---

## Resumen por severidad

> Convención de estado: 🔴 Abierto · 🟡 Fix entregado, sin confirmar · 🟢
> Resuelto (confirmado) · 🔵 Resuelto parcialmente.

| # | Hallazgo | Severidad | Estado |
|---|---|---|---|
| 9 | `sp_GetLoginByEmail` referencia tabla `Roles` inexistente (rompe login) — confirmado en vivo | 🔴 Alta | 🟢 Resuelto |
| 13 | Usuarios aprovisionados veían/se conectaban a BDs de otros usuarios | 🔴 Alta | 🔵 Parcial (SQL Server/Postgres/Mongo 🟢, MySQL 🔴) |
| 14 | Enumeración de cuentas OAuth-only en `POST /auth/login` (mensaje distinto sin necesitar password) | 🔴 Alta | 🟢 Resuelto |
| 16 | BD MySQL de usuarios OAuth quedaba con contraseña imposible de entregar (huérfana) | 🔴 Alta | 🟢 Resuelto |
| 11 | Ciclo de vida (TTL) sin implementar: no hay pausado/eliminación automática por inactividad | 🔴 Alta | 🔴 Abierto |
| 1 | Token JWT en query string del redirect OAuth | 🔴 Alta | 🔴 Abierto |
| 15 | Redirect OAuth también expone email/nombre/rol/userId en query string (amplía ítem 1) | 🔴 Alta | 🔴 Abierto |
| 3 | Secretos en texto plano en `appsettings.json` | 🔴 Alta (higiene) | 🔴 Abierto |
| 10 | Cuota de almacenamiento no se hace cumplir en Postgres/MySQL/Mongo (solo SQL Server) | 🟠 Media | 🔴 Abierto |
| 12 | Límite de conexiones concurrentes sin equivalente nativo en SQL Server/Mongo | 🟠 Media | 🔵 Parcial (MySQL/Postgres 🟢) |
| 4 | Vulnerabilidad conocida en `Microsoft.OpenApi` | 🟠 Media | 🔴 Abierto |
| 2 | Sin rate limit dedicado en endpoints OAuth | 🟠 Media | 🔴 Abierto |
| 2b | Auto-actualización continua de la documentación | Proceso | 🔵 Parcial (convención + scheduled task) |
| 5 | `CurrentSizeMB` sin tipo de columna (truncamiento silencioso) | 🟡 Baja-Media | 🔴 Abierto |
| 7 | Claim `"UserId"` duplicado como string literal | 🟡 Baja | 🔴 Abierto |
| 6 | `idempotencia.http` con endpoint obsoleto | ⚪ Cosmético | 🔴 Abierto |
| 8 | Doc desactualizada sobre callback OAuth | ⚪ Ya corregido en esta revisión | 🟢 Resuelto |
| 17 | `redirect_uri` de OAuth en `http://` en vez de `https://` detrás del reverse proxy (QA) — rompía el login completo | 🔴 Alta | 🟡 Fix entregado, pendiente confirmar en QA |
| 18 | Validación de entrada débil en Email/FullName/DbName/Engine — endurecida | 🟡 Media (defensa en profundidad) | 🟢 Resuelto |
| 19 | Ciclo de vida manual de BD (detalle/desactivar/eliminar/reset password) — feature nueva | Feature | 🟡 Código completo, pendiente desplegar SPs + configurar SMTP |
