# Historial del proyecto — contexto y mejoras

> **Qué es este documento:** un historial acumulativo, en orden cronológico,
> de decisiones, prompts importantes, bugs encontrados/corregidos y mejoras
> implementadas. No reemplaza a `routes.md` (estado de rutas), `API.md` (guía
> de consumo) ni `bugs.md` (hallazgos detallados con solución propuesta) — los
> referencia. Su propósito es que cualquier persona (o sesión de Claude)
> nueva pueda leer esto de arriba a abajo y entender **cómo llegó el proyecto
> a su estado actual y qué sigue pendiente**, sin tener que reconstruir el
> contexto desde cero.
> recuerda auto actualizar los .md tanto de bugs cambiando sus estados a resueltos ol si continuan y los otros que trabajen en paralelo o en segundo plano para que se actuallicen continuamente

## Convención (léela antes de agregar una entrada nueva)

- **Agrega una entrada nueva al final** cada vez que se complete un cambio
  significativo: una feature, un bug corregido, una decisión de arquitectura,
  o un hallazgo de seguridad relevante. No edites entradas viejas salvo para
  corregir un error factual (deja constancia de la corrección).
- Cada entrada lleva: fecha, un resumen de 1-2 líneas del pedido/objetivo, qué
  se hizo (con enlaces a archivos), qué quedó pendiente o abierto, y a qué
  otros documentos apunta (`routes.md`/`API.md`/`bugs.md`) si aplica.
- Al final del documento hay una sección **"Backlog / próximos pasos"** que
  se mantiene actualizada: cuando una entrada nueva resuelve algo del backlog,
  se saca de ahí; cuando descubre algo nuevo pendiente, se agrega.
- Este archivo vive en `docs/claude.md` (documentación de contexto/historial).
  Las reglas operativas del proyecto (qué actualizar cuando cambian las rutas,
  etc.) viven en `CLAUDE.md` en la raíz — ese archivo también recuerda
  mantener esta bitácora al día.

---

## Sesión 1 — 2026-07-XX (kickoff inicial)

**Pedido original** (prompt de arranque, 3 agentes en paralelo):

```
Contexto: backend para una app de usuarios cuyo fin es inscribirse y generar
bases de datos.

Agente 1: Analizar completamente el proyecto, verificar cada endpoint (
funcional o no), crear routes.md con tabla de rutas escaneadas, actualizar
API.md.

Agente 2: Todo cambio de rutas se refleja en routes.md; debe auto-actualizarse
apenas se note un cambio.

Agente 3: Buscar posibles bugs (tipo de problema + solución propuesta) en
bugs.md.
```

**Qué se hizo:** se creó `docs/routes.md` (tabla de 8 endpoints escaneados,
`AuthController` + `DatabasesController`) y `docs/bugs.md` (8 hallazgos
iniciales: token JWT en query string, falta de rate limit en OAuth, secretos
en texto plano, vulnerabilidad de `Microsoft.OpenApi`, `CurrentSizeMB` sin
tipo de columna, claim `UserId` duplicado, `idempotencia.http` obsoleto, doc
desactualizada del callback OAuth — corregida en la misma revisión). Se
estableció en `CLAUDE.md` (raíz) la convención de mantener `routes.md`/`API.md`
sincronizados con `Controllers/` en el mismo cambio.

**Pendiente al cierre de esta sesión:** no se pudo probar contra la BD real
(sin conectividad desde el entorno de análisis); `StatisticsController` no
existía todavía.

---

## Sesión 2 — 2026-07-21 (verificación de endpoints contra la BD real + aprovisionamiento MySQL)

**Pedido:** "lee el CLAUDE.md, verifica cada uno de los endpoints con la base
de datos" → luego, en la misma conversación: cambio de IP de la BD, debug de
errores reales de `dotnet run`/`dotnet watch` contra el servidor real, y
finalmente implementar el aprovisionamiento automático de BD MySQL en el
primer login + una lista de controles de seguridad.

**Qué se hizo (en orden):**

1. **Verificación de endpoints vs. código**: se detectó que `StatisticsController`
   (`GET /statistics`) existía en el código (sin commitear) pero no estaba
   documentado en `routes.md`/`API.md`. Se agregó (total: 9 endpoints de
   negocio). Se confirmó que la conectividad SQL desde el sandbox de análisis
   sigue bloqueada por diseño (ruleset de red, no un problema del servidor).
2. **Bugs reales en `appsettings.json`** (detectados en vivo al correr
   `dotnet run`/`dotnet watch` en la máquina del usuario):
   - JSON inválido (`}c` sobrante al final del archivo) tras un cambio manual
     de IP de la BD (`46.224.101.88` → `100.99.206.50`).
   - `Provisioning:Mongo:AdminConnectionString` había quedado con la IP vieja.
   - `ConnectionStrings:Colmena` tenía `Server=d` (typo) en vez de la IP real
     — causaba `SqlException Error 40` (Named Pipes / servidor no encontrado).
   Los tres se corrigieron.
3. **Bug confirmado en `sp_GetLoginByEmail`** (`docs/bugs.md` ítem 9): una vez
   resuelta la conexión, `POST /auth/login` falló con `Invalid object name
   'Roles'`. Se pidió el `sp_helptext` real, se confirmó que el SP hace
   `INNER JOIN Roles r ON r.RoleId = u.RoleId` pero el esquema real (visto en
   el diagrama ER del usuario) no tiene tabla `Roles` — `Users.Role` es una
   columna string directa. Se entregó el `CREATE OR ALTER PROCEDURE` corregido
   (`fix_sp_GetLoginByEmail.sql`) más el hallazgo secundario del alias de
   columna (`RoleName` vs. `Role` que espera `Models/LoginInfo.cs`).
   **Pendiente:** confirmar si `sp_RegisterUser`/`sp_UpsertExternalLogin`
   tienen el mismo patrón (también devuelven `Role`).
4. **Aprovisionamiento automático de BD MySQL en el primer login**
   (requisito de negocio). Se descubrió que `MySqlProvisioner`,
   `PostgresProvisioner` y `MongoProvisioner` **ya no eran stubs** (alguien ya
   los había implementado; los docs estaban desactualizados — corregido en
   `routes.md`/`bugs.md`). Se implementó:
   - `Services/AuthService.cs`: `EnsureMySqlDatabaseAsync` — se llama desde
     `RegisterAsync`, `LoginAsync` y `ExternalLoginAsync`; revisa
     `sp_GetUserDatabases` y, si el usuario no tiene ninguna BD MySQL, la
     aprovisiona (`DatabaseEngine.MySql`, dbName lógico `"principal"`). No
     bloquea el login si falla (se loguea el error).
   - `DTOs/AuthDtos.cs`: nuevo campo `AuthResponse.MySqlDatabase`
     (`ProvisionedDatabaseCredentials`) con las credenciales, poblado solo la
     vez que se crea.
5. **Controles de seguridad** (ver tabla de requisitos del usuario):
   - ✅ **Rate limiting en `POST /databases`**: nueva política
     `db-provisioning` (5/min por usuario, partición por claim `UserId` en vez
     de IP) en `Program.cs`, aplicada con `[EnableRateLimiting]` en
     `DatabasesController.Create`.
   - ✅ **Límite de conexiones concurrentes**: `MAX_USER_CONNECTIONS` (MySQL) y
     `CONNECTION LIMIT` (Postgres), configurable vía
     `Provisioning:{Engine}:MaxConcurrentConnections` (default 5). SQL Server y
     Mongo no tienen equivalente nativo simple — documentado como pendiente en
     `bugs.md` ítem 12 (propuesta: logon trigger en SQL Server, no aplicado sin
     poder probarlo en vivo).
   - ✅ **Permisos acotados**: ya cumplido de antes (`GRANT ... ON db.*`, nunca
     `*.*`).
   - ✅ **Prevención de SQL injection**: ya cumplido de antes (solo
     `FromSqlRaw` + `SqlParameter` tipados en el catálogo; los provisioners
     citan identificadores como defensa en profundidad ya que los DDL de
     creación de BD no admiten parámetros nativos en ningún motor).
   - ⚠️ **Cuota de almacenamiento** (Postgres/MySQL/Mongo): NO implementada —
     ningún motor además de SQL Server tiene un `MAXSIZE` nativo por BD.
     Documentado en detalle en `bugs.md` ítem 10 con diseño propuesto (job de
     monitoreo periódico).
   - ⚠️ **Ciclo de vida / TTL** (pausar o eliminar por inactividad): NO
     implementado — no existe job, ni SPs de catálogo para pausar/eliminar, ni
     lógica de actualización de `LastActivityAt`. Documentado en detalle en
     `bugs.md` ítem 11 con diseño propuesto (nuevas SPs +
     `IHostedService`).
6. **Docs actualizados**: `routes.md`, `API.md` (nueva sección 3.1 sobre
   auto-aprovisionamiento, corrección de la tabla de motores/rate limit) y
   `bugs.md` (ítems 9-12).

**Por qué se documentó en vez de implementarse todo:** los ítems 10 y 11
requieren correr contra una base de datos real para poder validarse (crear
datos de prueba, verificar que el job realmente pausa/revoca, etc.), y el
entorno de análisis de esta sesión no tiene conectividad a los motores reales.
Se prefirió dejar un diseño concreto y accionable en `bugs.md` antes que
código sin poder probarse.

---

## Sesión 3 — 2026-07-21 (fix confirmado en vivo + estados en bugs.md + tarea programada)

**Pedido:** agregar un campo de estado a cada hallazgo de `bugs.md`
(resuelto/abierto) y dejar `routes.md`/`API.md`/`bugs.md`/`claude.md`
actualizándose solos en segundo plano, no solo cuando se pide manualmente en
el chat.

**Qué se hizo:**

1. `docs/bugs.md`: se agregó un campo **Estado** (🔴 Abierto / 🟡 Fix
   entregado sin confirmar / 🟢 Resuelto / 🔵 Parcial) a los 12 hallazgos y a
   la tabla resumen.
2. Se creó la tarea programada `idempotencia-docs-sync` (cada 30 minutos):
   revisa `Controllers/` contra `routes.md`/`API.md`, revisa cada ítem de
   `bugs.md` contra el código fuente actual y ajusta su estado si es
   verificable sin acceso a la BD real, y solo si hubo cambios reales agrega
   una entrada aquí. No puede tocar código fuente, solo los 4 `.md`; y nunca
   marca como resuelto algo que solo se confirma ejecutando SQL contra el
   servidor real (eso sigue siendo manual, vía chat).
3. **`sp_GetLoginByEmail` (ítem 9 de `bugs.md`) confirmado RESUELTO en vivo**:
   el usuario corrió `dotnet watch` contra la BD real y el log mostró
   `EXEC sp_GetLoginByEmail @Email` ejecutando con éxito (ya no lanza
   `Invalid object name 'Roles'`). El mismo log mostró `sp_RegisterUser`
   ejecutando correctamente (incluye el caso esperado `El correo ya está
   registrado` → mapeado a `400` por `ApiExceptionMapper`, que ya trata
   `SqlException.Number >= 50000` como error de negocio — funciona como se
   documentó, no es un bug) y `sp_GetUserDatabases` corriendo dentro del flujo
   de auto-aprovisionamiento de `EnsureMySqlDatabaseAsync`. Con esto,
   `sp_RegisterUser` queda descartado del hallazgo (no tiene el bug de
   `Roles`); solo falta confirmar `sp_UpsertExternalLogin`.
4. **Aclarado (no era un bug de código)**: en esa misma corrida, `dotnet
   watch` mostró `error CS0103: El nombre 'EnsureMySqlDatabaseAsync' no existe
   en el contexto actual` seguido de un crash interno de Roslyn
   (`ArgumentNullException` en `AsyncMethodToStateMachineRewriter`) y el
   proceso se cayó. Se releyó `Services/AuthService.cs` completo y el método
   está correctamente definido y referenciado — es una falla conocida de Hot
   Reload de `dotnet watch` al aplicar en caliente la adición de un nuevo
   método async llamado desde varios lugares (agravado si los cambios de
   archivo llegan con latencia, ej. carpeta sincronizada). El error de "Failed
   to fetch / CORS" que vio el frontend justo después fue simplemente el
   backend caído, no un problema de CORS real. **Solución:** reiniciar
   `dotnet watch` (o usar `dotnet run` simple) en vez de confiar en hot reload
   para cambios que agregan métodos nuevos con `await`.

---

## Sesión 4 — 2026-07-21 (CORS a config, aislamiento entre BDs, concurrencia configurable, auditoría de datos sensibles)

**Pedido:** mover CORS a `appsettings.json`; corregir que los usuarios
aprovisionados podían ver/conectarse a BDs de otros usuarios; hacer
`maxConcurrentConnections` configurable al crear una BD; hacer un
exploratorio de qué devuelve cada endpoint para evitar exponer información
sensible, y generar una guía de consumo completa para el frontend (errores,
respuestas, excepciones, cómo consumir cada parte).

**Qué se hizo:**

1. **CORS movido a config**: `Cors:AllowedOrigins` en `appsettings.json`;
   `Program.cs` lanza error claro al arrancar si está vacío en vez de fallar
   en silencio.
2. **Aislamiento entre BDs de usuarios distintos** (`bugs.md` ítem 13):
   - SQL Server: `DENY VIEW ANY DATABASE` al login nuevo (antes veía los
     nombres de TODAS las BDs del servidor en `sys.databases`).
   - Postgres: `REVOKE CONNECT ... FROM PUBLIC` + `GRANT CONNECT ... TO`
     dueño en la BD nueva (antes cualquier rol podía conectarse a la BD de
     otro estudiante — privilegio `CONNECT` a `PUBLIC` por defecto).
   - MySQL: limitación conocida del motor, documentada, no corregible sin
     vistas custom sobre `information_schema`.
   - Mongo: ya estaba bien (roles ya scoped por BD).
3. **`maxConcurrentConnections` configurable por request** en
   `POST /databases` (`bugs.md` ítem 12, actualizado): el cliente puede
   pedirlo, pero `DatabaseProvisioningService.ResolveMaxConcurrentConnections`
   SIEMPRE lo acota a `Provisioning:{Engine}:MaxConcurrentConnectionsCap`
   (20) — nunca se confía en el número que pide el cliente sin límite.
4. **Auditoría de datos sensibles** (a pedido del usuario) sobre todas las
   respuestas de la API. Encontró y corrigió dos bugs reales:
   - **Ítem 14**: `POST /auth/login` revelaba, sin necesitar la contraseña
     correcta, si un correo existía y si era una cuenta OAuth-only (mensaje
     distinto antes de verificar el password). Corregido: mismo mensaje
     genérico `"Credenciales inválidas."` para los tres casos indistinguibles.
   - **Ítem 16**: el login OAuth SÍ auto-aprovisionaba la BD MySQL, pero su
     `AuthResponse` nunca se serializa como JSON (viaja por redirect), así
     que la contraseña real generada se perdía para siempre — una BD
     huérfana e inutilizable. Corregido quitando el auto-aprovisionamiento de
     `ExternalLoginAsync`; el frontend debe pedir `POST /databases`
     explícitamente tras un primer login OAuth (documentado en `API.md` §5.4).
   - **Ítem 15** (documentado, no corregido): el redirect OAuth expone no
     solo el token sino también `email`/`fullName`/`role`/`userId` en la
     query string — amplía el ítem 1, mismo fix pendiente (código de un solo
     uso).
   - Confirmado que ninguna respuesta expone `PasswordHash`, datos de otros
     usuarios, ni detalle interno de excepciones (siempre mensajes
     genéricos en 500).
5. **`docs/API.md` reescrito** como guía de consumo completa: por cada
   endpoint — body/validación, respuesta de éxito con tabla de campos, tabla
   exhaustiva de errores/excepciones posibles (código + mensaje exacto),
   ejemplos `fetch` actualizados (incluye manejo de OAuth + auto-pedido de
   MySQL), y una sección nueva (§3) que resume qué dato es sensible y por qué
   en cada respuesta.

**Pendiente:** nada nuevo se agregó al backlog salvo lo ya listado arriba
(ítem 15 sigue abierto, mismo fix que el ítem 1).

---

## Sesión 5 — 2026-07-22 (fix de `redirect_uri_mismatch` en OAuth de QA + documentación para Docusaurus)

**Pedido:** el usuario reportó `Error 400: invalid_request` / luego
`redirect_uri_mismatch` al intentar iniciar sesión con Google en el ambiente
de QA (`docs.idempotencia.andrescortes.dev`). En paralelo, pidió generar
documentación del backend para subir a un sitio Docusaurus.

**Qué se hizo:**

1. **Documentación para Docusaurus**: se generó un set de páginas markdown
   (visión general, autenticación, referencia de la API, aprovisionamiento
   de BD, manejo de errores/rate limiting, configuración por ambiente,
   seguridad y pendientes) a partir del código real y de `docs/API.md`,
   `docs/routes.md` y `docs/bugs.md` existentes. Los secretos reales de
   `appsettings.json` se excluyeron deliberadamente (ver `bugs.md` ítem 3).
   Luego se consolidó todo en un único archivo (`idempotencia-back-docusaurus.md`)
   a pedido del usuario, para pasárselo directo a quien arma el sitio.
2. **Diagnóstico de `redirect_uri_mismatch`** (`docs/bugs.md` ítem 17,
   nuevo): revisando la petición de red del usuario se confirmó que
   `GET /auth/google/login` respondía `302` correctamente (comportamiento
   esperado, no el bug), pero el `redirect_uri` que Google recibía era
   `http://docs.idempotencia.andrescortes.dev/signin-google` en vez de
   `https://...` (lo registrado en Google Cloud Console). Causa: el backend
   corre detrás de un reverse proxy en QA que termina TLS y reenvía como HTTP
   plano, y `Program.cs` no tenía `ForwardedHeaders` configurado — ASP.NET
   Core no sabía que el esquema original era `https` al armar el `Challenge`
   de `Microsoft.AspNetCore.Authentication.Google`.
3. **Fix aplicado en `Program.cs`**: se agregó
   `builder.Services.Configure<ForwardedHeadersOptions>(...)` (con
   `X-Forwarded-For` + `X-Forwarded-Proto`, `KnownNetworks`/`KnownProxies`
   vaciados porque el proxy real no está en `localhost`) y
   `app.UseForwardedHeaders()` como primera línea del pipeline, antes de
   `UseHttpsRedirection`/autenticación. Efecto secundario positivo: también
   resuelve la limitación ya documentada de que el rate limiting por IP
   colapsaba detrás de un proxy (README.md, sección de rate limiting,
   actualizada de "pendiente" a "resuelto").
4. **Docs actualizados**: `docs/bugs.md` (ítem 17 nuevo, estado 🟡 fix
   entregado — falta confirmar en vivo tras desplegar), `README.md` (nota de
   rate limiting tras proxy, ahora ✅), esta entrada.

**Pendiente:** el usuario debe desplegar el cambio en QA y confirmar que
`redirect_uri` ya sale como `https://` y que el login con Google completa sin
error, para pasar el ítem 17 de 🟡 a 🟢.

---

## Sesión 6 — 2026-07-22 (fix de `sp_UpsertExternalLogin`, guía de creación de BD para el front, endurecimiento de validación de entrada)

**Pedido:** confirmar y arreglar el error genérico que devolvía el callback
OAuth (`?error=Ocurrió un error al procesar la solicitud.`); generar una guía
para el frontend específica del flujo de creación de bases de datos; y
validar mejor los datos que manda el usuario (correos, espacios, nombres de
BD) para evitar SQL injection y proteger esos datos.

**Qué se hizo:**

1. **`sp_UpsertExternalLogin` confirmado con el mismo bug que
   `sp_GetLoginByEmail`** (ítem 9 de `bugs.md`, actualizado): el usuario pasó
   el `sp_helptext` real y se confirmó el mismo `JOIN`/`INSERT` contra una
   tabla `Roles` inexistente, más un segundo bug propio (`RoleName` en vez de
   `Role` en el SELECT final, que no coincide con `Models/UserIdentity.cs`).
   Se entregó el `CREATE OR ALTER PROCEDURE` corregido. **Pendiente:** que el
   usuario lo aplique contra la BD real y confirme el login OAuth.
2. **Guía de creación de bases de datos para el frontend**
   (`docusaurus-docs/guia-creacion-bases-de-datos.md`): cuándo se crea sola
   la BD (login/registro por contraseña) vs. cuándo hay que pedirla manual
   (OAuth), referencia completa de `POST /databases`, por qué la contraseña
   solo se entrega una vez, cómo se resuelve `maxConcurrentConnections`,
   errores a manejar, y un checklist final.
3. **Endurecimiento de validación de entrada** (`bugs.md` ítem 18, nuevo):
   se agregaron `[RegularExpression]` + normalización (trim, minúsculas en
   email, colapso de espacios en nombres) a `RegisterRequest`/`LoginRequest`
   (`Email`, `FullName`) y `CreateDatabaseRequest` (`Engine`, `DbName`), más
   un helper compartido `DTOs/InputNormalization.cs`. La misma normalización
   se aplicó a los datos que llegan de los callbacks OAuth en
   `AuthController`. Motivación: `DbName`/`Engine` terminan formando parte de
   DDL crudo en los provisioners (ya protegido con escape de identificadores
   por motor, pero sin validación de formato previa en el DTO); los cambios
   son una capa adicional de defensa en profundidad, no reemplazan las
   protecciones ya correctas (parámetros tipados en todo el acceso al
   catálogo). Documentado en `docs/API.md` y en la guía de creación de BD.

**Pendiente:** confirmar en vivo el fix de `sp_UpsertExternalLogin` (login
OAuth completo de punta a punta); considerar si conviene bajar el
`MaxLength` de `DbName` (hoy 128) para dejar margen seguro bajo el límite de
identificador de 64 caracteres de MySQL una vez aplicado el prefijo por
usuario — no se cambió en esta sesión para no alterar un límite ya
documentado públicamente sin coordinarlo primero.

---

## Sesión 7 — 2026-07-22 (ciclo de vida manual de bases de datos: detalle, desactivar, eliminar, reset de contraseña por correo)

**Pedido:** el usuario preguntó cómo ven los usuarios los datos de su BD, y
pidió: un endpoint de detalle para cuando se pierden las credenciales, un
endpoint para desactivar la BD, poder eliminarla solo si ya está inactiva, y
resetear la contraseña enviando la nueva por correo.

**Decisiones confirmadas con el usuario antes de implementar** (vía
pregunta): SMTP de Gmail/Workspace para el correo; desactivar = revocar
acceso físico real (no solo un flag); eliminar = borrado físico real
(irreversible).

**Qué se hizo:**

1. **Respuesta conceptual**: los usuarios ven los datos de su BD conectándose
   directo al motor con su propio cliente (MySQL Workbench, pgAdmin, Compass,
   SSMS, etc.) usando `host`/`port`/`loginName`/`password` — el backend nunca
   actúa como proxy de datos.
2. **4 endpoints nuevos en `DatabasesController`**: `GET /databases/{id}`
   (detalle, sin password), `POST /databases/{id}/deactivate` (revoca acceso
   físico), `DELETE /databases/{id}` (borrado físico real, solo si
   `Inactive`), `POST /databases/{id}/reset-password` (nueva contraseña
   enviada SOLO por correo, nunca en la respuesta HTTP).
3. **`IDatabaseProvisioner` extendido** (los 4 provisioners): `Host`/`Port`
   como propiedades (derivadas de config, no del catálogo — el host/puerto es
   el mismo para todas las BDs de un motor/ambiente), `ChangePasswordAsync` y
   `DeactivateAsync` (implementados con `ALTER LOGIN`/`ACCOUNT LOCK`/`NOLOGIN`/
   vaciar roles de Mongo según el motor).
4. **Servicio de correo nuevo**: `IEmailService`/`SmtpEmailService` (MailKit,
   no `System.Net.Mail` que está desaconsejado), `EmailSettings` (sección
   `Email` en `appsettings.json`, hoy con placeholders), `EmailTemplates`
   (HTML del correo de reset de contraseña).
5. **`NotFoundException` (404)** nueva en `Middleware/AppExceptions.cs` — "no
   existe" y "no es tuyo" devuelven el mismo mensaje a propósito.
6. **4 Stored Procedures nuevos** entregados como script
   ([`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql),
   no versionados como parte de la app por la arquitectura database-centric):
   `sp_GetDatabaseDetail`, `sp_DeactivateDatabase`, `sp_DeleteDatabase`,
   `sp_ResetDatabasePassword`. Asumen que `ProvisionedDatabases` ya tiene una
   columna `LoginName` (el script trae instrucciones de `ALTER TABLE` si no).
7. **Corrección menor de paso**: `POST /databases` armaba mal el header
   `Location` (apuntaba a `GetMine`, que no acepta `id`) — ahora apunta a
   `GetDetail`, que sí es un endpoint de recurso único.
8. **Docs actualizados**: `routes.md` (13 endpoints de negocio, antes 9),
   `API.md` (secciones 6.3–6.6 nuevas + auditoría de datos sensibles),
   `bugs.md` (ítem 19, nuevo).

**Pendiente — 2 pasos manuales antes de que esto funcione en un ambiente
real** (ninguno lo puede hacer el backend solo):
1. Correr el script SQL contra la base real.
2. Configurar `Email` en `appsettings.json` con una cuenta SMTP real (App
   Password de Google, no la contraseña normal) — idealmente vía User
   Secrets/variables de entorno, no en texto plano (mismo criterio que el
   ítem 3 de `bugs.md`).

Después de eso, confirmar en vivo los 4 endpoints.

## Sesión 8 — 2026-07-22 (dos bugs de validación reportados por QA: email de 150 y contraseña máx. 12 — registrados en la bitácora)

**Contexto:** al correr esta tarea programada de mantenimiento de
documentación, se encontró que `docs/bugs.md` ya tenía los ítems 20 y 21
completamente escritos (con estado 🟢 Resuelto) y que `docs/API.md`,
`README.md` y las guías de Docusaurus ya reflejaban ambos fixes — pero nunca
se agregó una entrada a esta bitácora para esa sesión de trabajo. Se
completa acá el registro correspondiente, sin tocar código (ya estaba
aplicado) ni el resto de los docs (ya estaban al día).

**Qué se hizo (en la sesión original, reconstruido de `bugs.md`):**

1. **Ítem 20 — `POST /auth/register` rechazaba un correo de exactamente 150
   caracteres** (el máximo documentado): tres validadores independientes
   apilados sobre `Email` (`[EmailAddress]`, `[RegularExpression]`,
   `[MaxLength(150)]`) no garantizaban coincidir exactamente en el límite.
   Se centralizó la validación en `RegisterRequest`/`LoginRequest` vía
   `IValidatableObject` + una clase interna `EmailValidation` (reglas
   explícitas: obligatorio, longitud, formato), apoyada en dos métodos
   nuevos de `DTOs/InputNormalization.cs`
   (`IsWithinMaxEmailLength`/`IsValidEmailFormat`).
2. **Ítem 21 — `POST /auth/register` aceptaba contraseñas de más de 12
   caracteres**, reportado por un ticket de QA con grabación adjunta. El
   requisito de negocio real es 8–12 caracteres, pero el código tenía
   `[MaxLength(100)]`. Se corrigió a `[MaxLength(12)]` en
   `RegisterRequest.Password`. A propósito no se tocó `LoginRequest.Password`
   (el login no debe re-validar longitud contra la política vigente de
   registro, para no bloquear cuentas creadas bajo un límite anterior).
3. **Docs actualizados en esa sesión** (confirmado, ya estaban así al
   iniciar esta tarea programada): `docs/API.md` (§5.1), `README.md` (tabla
   de errores de `/auth/register`), `docusaurus-docs/03-api-referencia.md` e
   `idempotencia-back-docusaurus.md` — todos decían "8–100 caracteres" para
   la contraseña, ahora dicen "8–12".

**Qué se hizo en esta corrida de la tarea programada:** solo se agregó esta
entrada a `docs/claude.md` para cerrar el registro; `docs/routes.md` y
`docs/API.md` ya estaban sincronizados con `Controllers/` (sin cambios de
rutas), y el resto de los ítems de `bugs.md` se revisaron contra el código
fuente actual sin encontrar más cambios de estado verificables sin acceso a
la BD real.

**Pendiente:** nada nuevo — ver "Backlog / próximos pasos" (sin cambios
respecto a la sesión 7).

---

## Sesión 9 — 2026-07-23 (corrida automática de mantenimiento de documentación — hallazgo: falta el script SQL del ciclo de vida)

**Contexto:** corrida programada de `idempotencia-docs-sync`. Se comparó
`Controllers/` contra `docs/routes.md`/`docs/API.md` (los 13 endpoints de
negocio y sus atributos de auth/rate-limit coinciden exactamente con el
código actual — sin cambios) y se revisó cada ítem de `docs/bugs.md` contra
el código fuente donde era verificable sin acceso a la BD real.

**Qué se encontró:** el archivo
[`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql),
referenciado desde `docs/bugs.md` (ítem 19) y `docs/routes.md` (hallazgo 11)
como el script pendiente de desplegar para los 4 SPs del ciclo de vida manual
de bases de datos, **no existe en el repositorio conectado** — no hay
carpeta `sql/` en el disco, y no está en `.gitignore` (a diferencia de
`appsettings.json`), así que no es un caso de "archivo local no versionado a
propósito": simplemente no está presente. No se puede saber desde esta tarea
si ya se aplicó y se borró, si nunca se guardó como archivo (solo se mostró
en el chat de otra sesión), o si se perdió. Se documentó como actualización
del ítem 19 en `bugs.md` y una nota en el hallazgo 11 de `routes.md`,
pidiendo confirmación en el chat.

**Verificaciones que confirmaron que nada más cambió de estado** (código
leído, sin necesidad de BD real): `Program.cs` mantiene `UseForwardedHeaders`
(ítem 17 sigue en 🟡, pendiente de confirmación en QA, no en 🔴); `AuthService.cs`
mantiene el mensaje genérico único en `LoginAsync` (ítem 14, 🟢) y no llama
`EnsureMySqlDatabaseAsync` desde `ExternalLoginAsync` (ítem 16, 🟢);
`DTOs/AuthDtos.cs` mantiene `MaxLength(12)` en `Password` y la validación
centralizada de `Email` vía `IValidatableObject` (ítems 20 y 21, 🟢);
`Provisioners/SqlServerProvisioner.cs` y `PostgresProvisioner.cs` mantienen
`DENY VIEW ANY DATABASE` / `REVOKE CONNECT`+`GRANT CONNECT` (ítem 13, sigue
🔵 parcial — MySQL sigue sin equivalente); no existe ningún `IHostedService`/
`BackgroundService` en el proyecto (ítem 11, TTL, sigue 🔴 abierto);
`Data/ColmenaDbContext.cs` sigue sin `HasColumnType` para `CurrentSizeMB`
(ítem 5, sigue 🔴). El ítem 9 (`sp_GetLoginByEmail`/`Roles`) y cualquier otro
hallazgo que dependa de ejecutar SQL contra la base real **no se tocó**, tal
como indica la regla de esta tarea programada.

**Nota sobre `appsettings.json`:** el archivo presente en el repositorio
conectado está vacío (solo el BOM, 3 bytes) en esta corrida — no se pudo usar
para verificar nada de la sección `Cors`/`Email`/connection strings. No se
registra como hallazgo nuevo porque ya se sabe (ítem 3 de `bugs.md`) que este
archivo vive fuera de git por diseño (secretos reales) y su contenido real
solo existe en la máquina del usuario; probablemente no llegó completo a este
entorno de análisis por la misma razón que ya limita la conectividad SQL
directa (ver notas de conectividad en `routes.md`).

**Pendiente:** confirmar en el chat si el script SQL del ciclo de vida ya se
aplicó contra la BD real (para poder mover el ítem 19 a 🟢) o si hay que
regenerarlo; el resto del backlog no cambia respecto a la sesión 8.

---

## Sesión 10 — 2026-07-23 (corrida automática de mantenimiento de documentación — hallazgo: SMTP ya configurado)

**Contexto:** otra corrida programada de `idempotencia-docs-sync` el mismo
día que la sesión 9. Se repitió la comparación de `Controllers/` contra
`docs/routes.md`/`docs/API.md` (los 13 endpoints y sus atributos de
auth/rate-limit siguen coincidiendo exactamente — sin cambios de rutas) y se
revisó `docs/bugs.md` ítem por ítem contra el código/config fuente donde era
verificable sin acceso a la BD real. `git status` mostraba casi todo el árbol
como "modified", pero `git diff` confirmó que es solo ruido de fin de línea
(CRLF/LF) sobre el mismo contenido del último commit — no hay cambios de
código reales en esta corrida.

**Qué se encontró:** `appsettings.json` en el repositorio conectado ya no
tiene placeholders en la sección `Email` — tiene una cuenta SMTP real de
Gmail configurada (`smtp.gmail.com`, usuario y una contraseña con formato de
App Password). Esto resuelve uno de los dos pasos manuales pendientes del
ítem 19 de `bugs.md` (ciclo de vida manual de bases de datos): el paso de
"configurar SMTP" ya está hecho; solo sigue faltando desplegar los 4 Stored
Procedures (`sql/2026-07-22_database_lifecycle_sps.sql` sigue sin existir en
el repo, ver sesión 9). Se actualizaron `docs/bugs.md` (ítem 19 y su
checklist de "Pendiente", ítem 3 con la mención del nuevo secreto en texto
plano), `docs/routes.md` (hallazgo 11) y `docs/API.md` (§10, fila de
`reset-password`) para reflejarlo.

**Verificaciones que confirmaron que nada más cambió de estado** (código
leído, sin necesidad de BD real): `Program.cs` sigue con `UseForwardedHeaders`
(ítem 17, sigue 🟡); `AuthController.cs` sigue sin `[EnableRateLimiting]` en
los 4 endpoints OAuth (ítem 2, sigue 🔴); el claim `"UserId"` sigue como
string literal duplicado en `DatabasesController.cs` y `JwtTokenService.cs`
(ítem 7, sigue 🔴); `ColmenaDbContext.cs` sigue sin `HasColumnType` para
`CurrentSizeMB` (ítem 5, sigue 🔴); no existe ningún `IHostedService`/
`BackgroundService` en el proyecto (ítem 11, TTL, sigue 🔴 abierto). El ítem 9
(`sp_GetLoginByEmail`/`Roles`) y cualquier otro hallazgo que dependa de
ejecutar SQL contra la base real no se tocó.

**Pendiente:** desplegar los 4 SPs del ciclo de vida de bases de datos
(único paso manual que falta para el ítem 19) y confirmar en vivo esos 4
endpoints; el resto del backlog no cambia respecto a la sesión 9.

---

## Sesión 11 — 2026-07-23 (fixes rápidos verificables sin BD + confirmación de 17 y 19)

**Pedido:** revisar el proyecto tomando el contexto de los `.md`, explicar los
hallazgos abiertos y hacer un lote de "fixes rápidos" confirmando cuáles son
errores reales. El usuario confirmó además que los ítems 17 y 19 de `bugs.md`
ya quedaron listos.

**Verificación previa (errores reales confirmados contra el código fuente):**
se leyeron `Controllers/AuthController.cs`, `Controllers/DatabasesController.cs`,
`Services/JwtTokenService.cs`, `Program.cs`, `Data/ColmenaDbContext.cs`,
`Models/ProvisionedDatabaseInfo.cs`, `Models/ProvisionedDatabaseDetail.cs`,
`idempotencia.csproj`, `idempotencia.http` y `OpenApi/BearerSecuritySchemeTransformer.cs`.
Los 5 hallazgos elegidos (ítems 2, 4, 5, 6, 7) se confirmaron como reales y
presentes en el código actual antes de tocar nada.

**Qué se hizo (fixes aplicados):**

1. **Ítem 6 (🟢) — `idempotencia.http`**: se reemplazó el único `GET /weatherforecast/`
   (resto de la plantilla de .NET) por peticiones reales a los 13 endpoints de
   negocio, con variable `@token` que reutiliza el JWT del login.
2. **Ítem 5 (🟢) — `CurrentSizeMB` sin tipo**: se agregó
   `HasColumnType("decimal(10,2)")` en `Data/ColmenaDbContext.cs` para
   `ProvisionedDatabaseInfo` y `ProvisionedDatabaseDetail` (ambos exponen el
   decimal). Elimina el warning de truncamiento silencioso de EF Core.
3. **Ítem 7 (🟢) — claim `"UserId"` literal duplicado**: se creó
   `Services/JwtClaimNames.cs` (`JwtClaimNames.UserId`) y se reemplazó el
   string literal en los 3 lugares que lo usaban (`JwtTokenService`,
   `DatabasesController`, la partición del rate limiter en `Program.cs`).
4. **Ítem 2 (🟢) — rate limit dedicado en OAuth**: nueva política `oauth`
   (20/min por IP) en `Program.cs` + `[EnableRateLimiting("oauth")]` en los 4
   endpoints OAuth de `AuthController`. Se prefirió una política propia en vez
   de reusar `auth` (10/min) para no mezclar la partición con login/registro.
5. **Ítem 4 (🟡) — `Microsoft.OpenApi` vulnerable (NU1903 / GHSA-v5pm-xwqc-g5wc)**:
   se agregó un `PackageReference` explícito a `Microsoft.OpenApi` **2.7.5**
   (primera versión parcheada de la línea 2.x) en `idempotencia.csproj`, que
   sobrescribe la 2.0.0 transitiva. Se verificó que la API usada por
   `BearerSecuritySchemeTransformer` sigue presente en 2.7.5. Queda 🟡 porque
   confirmar que el aviso desaparece requiere `dotnet restore` +
   `dotnet list package --vulnerable`, que no se pudo correr en esta sesión.

6. **Ítems 17 y 19 → 🟢 (confirmados por el usuario):** el usuario confirmó que
   el `redirect_uri_mismatch` de OAuth en QA (ítem 17) ya no ocurre tras
   desplegar, y que los 4 Stored Procedures del ciclo de vida manual (ítem 19)
   ya están desplegados contra la BD real y sus 4 endpoints funcionan de punta
   a punta. Se actualizaron sus estados en `bugs.md`, `routes.md` y `API.md`.

**Limitación importante de esta sesión:** el entorno de análisis **no tiene el
SDK de .NET ni salida de red al instalador**, así que **no se pudo compilar ni
arrancar la app**. Los fixes 2/5/6/7 se verificaron por lectura del código
(son cambios estándar y de bajo riesgo) y el ítem 4 por lectura + consulta del
aviso de seguridad. **Antes de dar por cerrados 2/4/5/7, correr un
`dotnet build` (y para el 4, `dotnet list package --vulnerable`) en la máquina
del usuario.**

**Docs actualizados:** `docs/bugs.md` (ítems 2, 4, 5, 6, 7 con solución
aplicada + estados; 17 y 19 a 🟢; tabla resumen), `docs/routes.md` (columna de
rate limit de los 4 endpoints OAuth + hallazgo 12 nuevo + estados 17/19),
`docs/API.md` (§5.3 nota de rate limit OAuth, §10 endpoints de ciclo de vida a
desplegados/confirmados) y esta bitácora.

---

## Sesión 12 — 2026-07-23 (revisión de 4 hallazgos reportados por el equipo de frontend)

**Pedido:** revisar 4 hallazgos que el front documentó tras integrar contra el
backend real (local y desplegado) y corregir los que apliquen.

**Resultado de la revisión (contra el código/config reales en disco):**

1. **Connection string a `Database=master` (hallazgo "bloqueante" del front):**
   verificado que el login funciona en vivo contra ese servidor desde la
   sesión 3, o sea el esquema está en `master` y la app funciona. El 500 que
   vio el front (07-17) es anterior a los arreglos de la sesión 2. **Decisión
   del usuario: dejarlo como está**; documentado como deuda técnica en
   `bugs.md` ítem 22 (nuevo, 🔵 conocido/aceptado). No se tocó `appsettings.json`.
2. **Callback OAuth devolvía JSON crudo:** ya está resuelto en el código
   committeado — `AuthController.ExternalCallback` redirige vía
   `OAuthRedirectBuilder.BuildSuccess` a `{Frontend:BaseUrl}/oauth/callback?...`
   (el diff local del front ya existe, más limpio). Pendiente solo de
   despliegue: setear `Frontend:BaseUrl` al dominio real en el appsettings del
   ambiente desplegado.
3. **400 con correo de 150 caracteres:** ya resuelto (ítem 20, validación
   centralizada e inclusiva `<= 150`). Se agregó nota de reconfirmación en
   `bugs.md`: si QA aún lo ve es contra el desplegado (va detrás), redesplegar.
4. **GitHub reingresa sin pedir credenciales tras logout:** comportamiento
   esperado de SSO, no un bug. Documentado en `bugs.md` ítem 23 (nuevo, 🟢 no
   es bug). GitHub OAuth no soporta `prompt=login`, no hay fix limpio de
   backend.

**Qué se hizo:** solo documentación (`bugs.md` ítems 22 y 23 nuevos + nota de
reconfirmación en el 20 + tabla resumen; esta bitácora). Ningún cambio de
código: de los 4 hallazgos, 2 ya estaban resueltos en el código, 1 es decisión
de config del usuario (dejar master) y 1 es comportamiento esperado.

**Pendiente clave que surge de esta revisión:** **redesplegar el backend
actual a QA**, porque varios fixes ya committeados (validación de email 150,
callback OAuth por redirect, rate limit OAuth de la sesión 11) no llegan a QA
hasta que se despliegue, y setear `Frontend:BaseUrl` al dominio real ahí.

---

## Sesión 13 — 2026-07-23 (OAuth auto-aprovisiona la BD MySQL y entrega las credenciales por correo)

**Pedido:** el usuario preguntó si convenía que el backend disparara el
auto-aprovisionamiento de la BD MySQL en login por OAuth o dejárselo al front.
Se le explicó que el nudo real es el canal seguro para la contraseña de un solo
uso (la respuesta OAuth viaja por redirect/query string). El usuario propuso
justo la solución: **mandarla por correo**. Se implementó.

**Qué se hizo:**

1. **`Services/EmailTemplates.cs`**: nueva plantilla
   `FirstDatabaseCredentials(ProvisionedDatabaseCredentials)` con host, puerto,
   BD, usuario y contraseña (más `using idempotencia.DTOs;`).
2. **`Services/AuthService.cs`**: se inyectó `IEmailService` (ya registrado en
   `Program.cs`), y `ExternalLoginAsync` volvió a llamar
   `EnsureMySqlDatabaseAsync`; si crea la BD, envía las credenciales por correo
   con un método nuevo `SendFirstDatabaseEmailAsync` (no bloqueante: si el
   correo falla, el login sigue, la BD existe y se puede regenerar la contraseña
   con `POST /databases/{id}/reset-password`). La contraseña NO se pobla en el
   `AuthResponse` (se perdería/expondría en el redirect). Esto **revierte
   parcialmente la decisión del ítem 16**: antes se quitó el auto-aprovisionamiento
   de OAuth; ahora se reactiva pero entregando por el canal seguro que faltaba.
3. **Docs**: `bugs.md` ítem 16 (solución inicial vs. final), `routes.md`
   (filas de callback + hallazgo 9), `docs/API.md` (§5.4 reescrita, ejemplo de
   callback, §10) y esta bitácora.

**Limitación:** igual que la sesión 11, no se pudo compilar/arrancar en este
entorno (sin SDK de .NET). Cambios verificados por lectura; falta `dotnet build`
y una prueba en vivo del correo (SMTP ya está configurado en `appsettings.json`).

---

## Backlog / próximos pasos

1. **Redesplegar el backend actual a QA** para que lleguen los fixes ya
   committeados que el front todavía ve fallar contra el desplegado
   (validación de email de 150 caracteres — ítem 20; callback OAuth por
   redirect; rate limit OAuth — ítem 2; auto-aprovisionamiento OAuth + correo —
   ítem 16, sesión 13) y **setear `Frontend:BaseUrl` al
   dominio real** (`https://idempotencia.andrescortes.dev`) en el appsettings
   del ambiente desplegado, no `localhost`.
2. **Confirmar la compilación tras los fixes de la sesión 11**: `dotnet build`
   (ítems 2/5/7) y, para el ítem 4, `dotnet restore` +
   `dotnet list package --vulnerable` para confirmar que `NU1903` desaparece
   con el pin de `Microsoft.OpenApi` 2.7.5 (mover el ítem 4 de 🟡 a 🟢). Incluye
   también los cambios de la sesión 13 (`AuthService`/`EmailTemplates`), y hacer
   una prueba en vivo de un primer login OAuth para confirmar que llega el correo
   con las credenciales de la BD.
3. **Migrar el connection string fuera de `master`** (deuda técnica, `bugs.md`
   ítem 22): cuando exista una BD dedicada con el esquema (tablas + SPs)
   desplegado, apuntar ahí `ConnectionStrings:Colmena` y el
   `Provisioning:SqlServer:AdminConnectionString`. Hoy funciona en master, no
   es urgente.
4. **Confirmar en vivo el fix de `sp_UpsertExternalLogin`** (ítem 9) tras
   aplicar el `ALTER PROCEDURE` en la BD real — login OAuth de punta a punta.
5. **Evaluar bajar el `MaxLength` de `CreateDatabaseRequest.DbName`** (hoy
   128) para dejar margen bajo el límite de 64 caracteres de MySQL.
6. **Ciclo de vida automático (TTL)**: `sp_GetIdleDatabases` +
   `DatabaseLifecycleJob` (`IHostedService`) reutilizando
   `sp_DeactivateDatabase`/`sp_DeleteDatabase` — ítem 11 de `bugs.md`.
7. **Endpoint de "reactivar" una BD desactivada** — hoy desactivar es
   unidireccional hacia eliminar.
8. **Cuota de almacenamiento real** para Postgres/MySQL/Mongo — ítem 10.
9. **Límite de conexiones concurrentes en SQL Server** vía logon trigger —
   ítem 12.
10. **Token JWT (y PII) en query string del redirect OAuth** — ítems 1 y 15
    (mismo fix: intercambio por código de un solo uso).
11. **Secretos reales en texto plano en `appsettings.json`** (incluye SMTP y,
    ahora explícito, las credenciales del `sa`) — ítem 3.

**Resueltos/cerrados en la sesión 12 (revisión del front):** callback OAuth por
redirect y validación de email 150 ya estaban en el código (falta redeploy a
QA); connection string a master aceptado como deuda técnica (ítem 22); GitHub
SSO documentado como comportamiento esperado (ítem 23).
