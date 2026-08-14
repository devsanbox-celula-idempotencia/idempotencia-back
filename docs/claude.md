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

## Sesión 14 — 2026-07-29 (QA: mensajes de login por caso, 547 al desactivar, y `currentSizeMB` deja de ser ficticio)

**Pedido:** tres cosas encadenadas, todas surgidas de QA. (1) que el login diga
"contraseña incorrecta" en vez del genérico "Credenciales inválidas"; (2)
diagnosticar un error que salía al desactivar una BD; (3) verificar si el
indicador de almacenamiento usado servía y, al confirmar que no, arreglarlo.

**Qué se hizo:**

1. **Mensajes de login específicos por caso** — decisión de producto que
   revierte a propósito el fix del ítem 14. `LoginAsync` en
   [`Services/AuthService.cs`](../Services/AuthService.cs) ahora distingue
   correo no registrado, cuenta OAuth-only, contraseña incorrecta y cuenta
   inactiva, cada uno con su 401 y su mensaje. Se dejó constancia del riesgo de
   enumeración reintroducido (mitigado solo por el rate limit `auth` de 10/min)
   en el ítem 14 y en `API.md`, junto con las alternativas por si más adelante
   se prioriza la privacidad. El formato de la respuesta no cambió, así que el
   front solo debe borrar cualquier comparación contra el string viejo.

2. **Ítem 24 (nuevo, abierto) — `POST /databases/{id}/deactivate` fallaba
   siempre con SQL 547.** Diagnosticado desde los logs del contenedor de QA:
   `CK_ProvDb_Status` permite `('Failed','Deleted','Paused','Active','Provisioning')`
   y `sp_DeactivateDatabase` escribe `'Inactive'`. El SP tiene razón y la
   constraint está desactualizada: `'Paused'` viene del vocabulario de la
   propuesta de TTL del ítem 11, que nunca se implementó, y nada en el código
   escribe ese valor. **Pendiente de aplicar el `ALTER TABLE`** documentado en
   el ítem 24. Efecto colateral a reparar: `DeactivateAsync` revoca el acceso
   físico antes de tocar el catálogo, así que hay al menos una BD con el login
   revocado pero marcada `Active`; se corrige reintentando el endpoint una vez
   aplicado el fix (los cuatro provisioners son idempotentes en
   `DeactivateAsync`).

3. **Ítem 25 (nuevo) — `currentSizeMB` era un campo muerto, ahora se
   sincroniza de verdad.** Confirmado con `sp_helptext`: `sp_GetDatabaseDetail`
   solo hace `SELECT` de la columna, y ningún SP ni job la escribía después de
   la creación. Implementado el camino completo de medición:
   - `GetSizeMbAsync` agregado a
     [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs)
     y a los cuatro provisioners, cada uno con la consulta nativa de su motor
     (`sys.master_files`, `information_schema.tables`, `pg_database_size`,
     `dbStats`). **En los motores de los estudiantes no se creó nada**: solo se
     les lanza una consulta con la conexión admin que ya existía.
   - Dos SPs nuevos en el catálogo, en
     [`sql/2026-07-29-size-sync.sql`](../sql/2026-07-29-size-sync.sql):
     `sp_GetDatabasesForSizeSync` (todas las BDs activas, sin filtro por
     usuario — el job no actúa en nombre de nadie) y `sp_UpdateDatabaseSize`.
     **Pendiente de ejecutar en la BD.**
   - Sus métodos en
     [`Interfaces/IDatabaseRepository.cs`](../Interfaces/IDatabaseRepository.cs)
     y [`Repository/DatabaseRepository.cs`](../Repository/DatabaseRepository.cs),
     con `Precision`/`Scale` explícitos en el parámetro decimal para no truncar.
   - [`Services/DatabaseSizeMonitor.cs`](../Services/DatabaseSizeMonitor.cs),
     un `BackgroundService` que cada 15 minutos (configurable) recorre las BDs
     activas, mide y persiste solo lo que cambió. Es de mejor esfuerzo: atrapa
     todo por base, tiene timeout por base, y nunca deja escapar una excepción
     que tumbe el host. Configurado por
     [`Services/SizeMonitorSettings.cs`](../Services/SizeMonitorSettings.cs)
     (sección `Provisioning:SizeMonitor`), con defaults que funcionan sin
     configuración alguna.
   - Registrado en [`Program.cs`](../Program.cs).

**Qué queda pendiente:**
- Ejecutar `sql/2026-07-29-size-sync.sql` y el `ALTER TABLE` del ítem 24 en la
  BD de QA. Sin el primero, el job falla en cada ciclo con "no existe el
  procedimiento" (queda en logs, no tumba nada).
- Verificar `sp_DeleteDatabase` por si su guarda también está escrita contra
  `'Paused'`.
- `dotnet build`: no se pudo compilar en este entorno (sin SDK de .NET ni en el
  contenedor ni en la máquina local). Todos los cambios están verificados por
  lectura.
- Reparar las BDs desincronizadas por el ítem 24 reintentando el deactivate.

**Nota sobre configuración:** `appsettings.json` no está versionado
(`.gitignore`), así que la sección `Provisioning:SizeMonitor` quedó documentada
en el ítem 25 de `bugs.md` en vez de agregarse a un archivo. No hace falta
tocarla para que el job corra: todos los valores tienen default.

4. **Ítem 26 (nuevo) — endpoint de reactivar, cierra el punto 7 del backlog.**
   Desactivar era un camino sin retorno aunque los datos nunca se borran: desde
   `Inactive` la única salida era `DELETE`. Se agregó
   `POST /databases/{id}/reactivate`, la inversa exacta: `ReactivateAsync` en
   [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs)
   y los cuatro provisioners (`ENABLE`, `ACCOUNT UNLOCK`, `LOGIN`, y en Mongo
   restituir `readWrite` sobre la propia BD),
   `sp_ReactivateDatabase` en
   [`sql/2026-07-29-reactivate.sql`](../sql/2026-07-29-reactivate.sql), y el
   endpoint en [`Controllers/DatabasesController.cs`](../Controllers/DatabasesController.cs).
   El orden motor-primero se eligió por la razón contraria a la de desactivar:
   deja el estado desincronizado *recuperable* (BD habilitada pero marcada
   `Inactive`, con el botón todavía visible) en vez del que dejaría al usuario
   atascado. Detalle completo en el ítem 26.

---

## Sesión 15 — 2026-07-30 (el `host` que se entrega al usuario deja de ser el host interno del motor)

**Pedido:** "¿de dónde se saca el host?" y, al ver que salía de
`Provisioning:{Engine}:Host` —que en despliegue termina con el nombre del
contenedor—, cambiarlo por un host estático configurable: una clave en
`appsettings.json` con la IP del VPS que se entregue como host de conexión.

**Qué se hizo:**

1. **Ítem 27 (nuevo) — separación entre host interno y host público.** El campo
   `host` de la API salía de `Provisioning:{Engine}:Host`, una clave que
   describía cómo llega el *backend* al motor: en Docker, el nombre del
   contenedor, que no resuelve desde la máquina del usuario. Y si la clave
   faltaba, caía a `localhost` en silencio. Se agregó
   [`Services/ProvisioningSettings.cs`](../Services/ProvisioningSettings.cs) con
   la clave global **`Provisioning:IpVps`** (IP pública del VPS o su dominio),
   los cuatro provisioners pasaron a recibir `IOptions<ProvisioningSettings>` y
   exponen `Host => IpVps`, y `Provisioning:{Engine}:Host` dejó de leerse. El
   host interno sigue viviendo dentro de cada `AdminConnectionString`, que es su
   lugar. Detalle completo en el ítem 27 de `bugs.md`.

2. **Decisiones tomadas explícitamente en esta sesión** (se ofrecieron
   alternativas y se eligió):
   - **Una clave global, no una por motor:** los cuatro motores corren en la
     misma máquina, así que repetir la IP cuatro veces solo agrega superficie
     para desincronizarse. Lo que sigue siendo por motor es el `Port`.
   - **Nombre `IpVps`** (sobre `PublicHost`): describe lo que es hoy. Si algún
     día pasa a ser un dominio, el valor sigue funcionando —
     `ProvisioningSettings` documenta que acepta ambos— y el rename queda como
     deuda cosmética.
   - **Error al arrancar si falta**, no warning + `localhost`: mismo criterio
     que `Cors:AllowedOrigins`. Un despliegue que arranca "bien" y entrega
     datos de conexión inservibles es justamente el modo de falla del ítem 27.

3. **Efecto secundario documentado:** el host no se persiste en el catálogo (se
   resuelve desde el provisioner en cada consulta), así que cambiar
   `Provisioning:IpVps` reescribe retroactivamente el host reportado de todas
   las BDs ya creadas. Es lo que se quiere si el VPS cambia de IP, pero conviene
   saberlo.

4. **Documentación actualizada:** ítem 27 + fila en el resumen de `bugs.md`,
   tabla de secciones de configuración y checklist de QA/producción en
   [`docusaurus-docs/06-configuracion-ambientes.md`](../docusaurus-docs/06-configuracion-ambientes.md),
   y el XML-doc de `Host`/`Port` en
   [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs).
   `routes.md` y `API.md` **no cambian**: no se tocó ninguna ruta ni el contrato
   de respuesta (el campo `host` sigue siendo el mismo campo, con el valor que
   siempre debió tener).

5. **`appsettings.json` reorganizado** (no versionado, así que queda constancia
   acá): se agregó `Provisioning:IpVps` con la IP del servidor
   (`100.99.206.50`, la misma que ya usaban `Frontend:BaseUrl`/`Cors` y los
   ejemplos de `API.md`), se **borraron las cuatro claves
   `Provisioning:{Engine}:Host`** —que tenían los nombres de contenedor
   `idempotencia-sqlserver`/`-postgres`/`-mysql`/`-mongodb`, la causa exacta del
   ítem 27—, se hizo explícito el bloque `Provisioning:SizeMonitor` con los
   mismos valores que sus defaults en código, y se reagruparon las secciones
   (frontend/CORS → auth → correo → datos). Ningún valor existente cambió; se
   verificó clave por clave. Los `AdminConnectionString` siguen apuntando a los
   nombres de contenedor, que es lo correcto: son la ruta interna.

**Pendiente al cierre:** `dotnet build` (no hubo SDK de .NET disponible en el
entorno de esta sesión; los cambios están verificados por lectura y diff) y
desplegar. En el ambiente desplegado hay que confirmar que `Provisioning:IpVps`
sea una dirección alcanzable **desde la máquina del usuario**: `100.99.206.50`
está en el rango CGNAT (100.64.0.0/10), típico de Tailscale, así que sirve si
los usuarios entran por la misma red privada, pero no desde internet abierta —
ahí tendría que ser la IP pública o un dominio.

---

## Sesión 16 — 2026-07-30 (MySQL: se va el paso manual de `allowPublicKeyRetrieval` y las credenciales dejan de viajar en claro)

**Pedido:** los usuarios nuevos tenían que activar a mano
`allowPublicKeyRetrieval` en su gestor para poder conectarse a su BD MySQL. El
diagnóstico que llegó: forzar TLS, reemplazando `useSSL=true` por
`sslMode=REQUIRED`, en la conexión del backend y en cualquier cadena que se le
entregue al usuario.

**Corrección del diagnóstico (antes de tocar nada):** `useSSL` **no existía en el
repo**. Es un parámetro de Connector/J, el driver Java que usa DBeaver; este
backend usa MySqlConnector (.NET), donde la opción se llama `SslMode` y `useSSL`
no sería válida. Y el backend **no le entregaba ninguna cadena de conexión al
usuario**: la API y el correo daban host/puerto/usuario/contraseña por separado.
O sea que no había un parámetro que corregir: había uno que agregar, y una cadena
que crear. El fondo del diagnóstico sí era correcto (faltaba forzar el cifrado) y
se aplicó completo.

**Qué se hizo** — ítem 28 de `bugs.md`, cuatro partes:

1. **La conexión propia del backend:** `SslMode=Required` en
   `Provisioning:MySql:AdminConnectionString`. Verificado en la doc de
   MySqlConnector que `Required` cifra **sin** validar el certificado (solo
   `VerifyCA`/`VerifyFull` validan) — necesario con el cert autofirmado. El
   default que estaba en uso era `Preferred`: cifra "si se puede", sin garantía.

2. **Cadenas de conexión para el usuario (nuevas):** `BuildClientConnection` en
   [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs)
   + los cuatro provisioners, devolviendo
   [`Models/ClientConnectionInfo.cs`](../Models/ClientConnectionInfo.cs). Se
   exponen como `connectionUri` (formato nativo, con credenciales
   percent-encoded — el alfabeto de `PasswordGenerator` incluye `#$%&*+-`) y
   `jdbcUrl` (para DBeaver/Workbench/DataGrip, sin credenciales, `null` en Mongo)
   en `POST /databases`, en el `mySqlDatabase` del login y en los **dos** correos
   de credenciales. `GET /databases/{id}` a propósito NO las trae: no hay
   contraseña para poner ahí.

3. **`CREATE USER ... REQUIRE SSL`** en MySQL: el motor rechaza conexiones sin
   cifrar de los usuarios aprovisionados. Es lo único que el cliente no puede
   eludir — el `ssl-mode` de la cadena es un pedido, no una garantía. Para los
   usuarios ya existentes queda
   [`sql/2026-07-30-mysql-require-ssl.sql`](../sql/2026-07-30-mysql-require-ssl.sql),
   que verifica primero, genera los `ALTER` para revisarlos antes de aplicarlos y
   documenta cómo revertir. **Es el primer script de `sql/` que corre contra
   MySQL y no contra el catálogo de SQL Server.**

4. **Flag `Provisioning:{Engine}:RequireTls`** para gobernar los puntos 2 y 3:
   `true` en MySQL y SqlServer, `false` en Postgres y Mongo. La razón de que no
   sea una constante: las imágenes oficiales de Postgres y Mongo **no habilitan
   TLS por defecto**, y exigirlo contra un motor sin certificado deja a esos
   usuarios sin poder conectarse. Se prefirió un flag por motor antes que
   arreglar MySQL y romper otros dos motores en silencio.

**Decisiones tomadas explícitamente en esta sesión:** entregar la cadena en los
cuatro motores (no solo MySQL) como campo nuevo en la API + los correos, en vez
de solo documentar el parámetro; y aplicar `REQUIRE SSL` del lado del servidor,
aceptando que un cliente sin TLS deje de conectarse (falla clara y explicable, en
vez de una garantía de cifrado que en realidad era voluntaria).

**Pendiente al cierre:**

- `dotnet build` (sigue sin haber SDK de .NET en el entorno de la sesión; los
  cambios están verificados por lectura y diff) y desplegar.
- Ejecutar el script de backfill en MySQL.
- **Postgres y Mongo siguen sin TLS**: habilitar certificado en esos contenedores
  y prender su `RequireTls`. Hasta entonces, el punto de seguridad queda cerrado
  solo para MySQL y SqlServer.
- Avisar al front de los dos campos nuevos —
  [`docs/cambios-api-frontend-2026-07-30.md`](cambios-api-frontend-2026-07-30.md)
  — y ojo: `connectionUri` **contiene la contraseña**, así que se trata con el
  mismo cuidado que `password` (no loguear, no persistir).

---

## Sesión 17 — 2026-08-12 (autoservicio de subdominios DNS sobre `coderhivex.com`)

> **Nota de corrección (misma sesión).** El primer planteo de esta sesión asumió
> que el servicio de DNS acompañaba al aprovisionamiento de bases de datos, con
> subdominios `{label}.idempotencia.andrescortes.dev`. El usuario aclaró a mitad
> de camino que **no es para las BDs**: es autoservicio de subdominios para los
> proyectos web de los usuarios, bajo `coderhivex.com` y con la célula (equipo de
> trabajo) como nivel intermedio. Lo que sigue describe el diseño final; los
> archivos de la primera versión fueron reescritos, no acumulados. El único
> artefacto que hay que mirar con cuidado es
> `sql/2026-08-12-dns-records.sql`, que **también se reescribió**: si alguien
> alcanzó a ejecutar la primera versión, ver el apartado "MIGRAR DESDE LA v1" de
> ese archivo.

**Requisito atendido** (punto 3 del documento del equipo, "Creación de Registros
DNS por parte de los Usuarios"):

```
[nombre-elegido-por-el-usuario].[nombre_de_la_celula].coderhivex.com
ej: airflow.idempotencia.coderhivex.com
```

Autoservicio desde el panel, automatizado contra la API del proveedor DNS, con
validaciones de colisión y cuota, HTTPS automático, control administrativo
(auditar/listar/revocar) y documentación en Docusaurus.

**Decisiones tomadas con el usuario:**

| Pregunta | Decisión |
|---|---|
| Origen de la célula | Texto validado por formato, **sin catálogo** ("no le prestes atención a eso") |
| Tipo de registro | **Solo A** (IPv4 pública del servicio del usuario) |
| HTTPS | **Proxied + Total TLS / ACM de Cloudflare** |
| Administración | Endpoints `/admin/dns` con rol `Admin` |

**Qué se hizo:**

- **Configuración** — sección `Dns` en [`appsettings.json`](../appsettings.json)
  enlazada a [`Services/DnsSettings.cs`](../Services/DnsSettings.cs): `ZoneId`,
  `ApiToken`, `ZoneName` (`coderhivex.com`), `RecordType`, `Proxied`,
  `TtlSeconds`, `RequestTimeoutSeconds`. Sobreescribibles por entorno
  (`Dns__ApiToken`, etc.). [`Program.cs`](../Program.cs) valida
  `ZoneId`/`ApiToken`/`ZoneName` al arrancar y **no levanta** si falta alguna —
  mismo criterio que `Provisioning:IpVps`.

- **Flujo de usuario** — [`Controllers/DnsController.cs`](../Controllers/DnsController.cs)
  (`POST/GET/GET{id}/PUT/DELETE /dns` + `GET /dns/zone`), orquestado por
  [`Services/DnsProvisioningService.cs`](../Services/DnsProvisioningService.cs)
  sobre [`Repository/DnsRepository.cs`](../Repository/DnsRepository.cs) (catálogo)
  y [`Provisioners/CloudflareDnsProvider.cs`](../Provisioners/CloudflareDnsProvider.cs)
  (API v4, `HttpClient` tipado).

- **Administración** — [`Controllers/AdminDnsController.cs`](../Controllers/AdminDnsController.cs):
  `GET /admin/dns` (inventario filtrable por célula, dueño, estado y días sin
  modificar), `GET /admin/dns/{id}` y `POST /admin/dns/{id}/revoke`.

- **Base de datos** — [`sql/2026-08-12-dns-records.sql`](../sql/2026-08-12-dns-records.sql):
  tablas `DnsRecords` y `DnsReservedLabels`, 4 índices y 10 SPs. Documento de
  cambios en [`docs/cambios-db-dns-2026-08-12.md`](cambios-db-dns-2026-08-12.md).

- **Documentación** — página nueva de Docusaurus
  `docusaurus-docs/08-dns-subdominios.md` con el flujo de creación por parte del
  usuario y el procedimiento de administración/revocación por parte del equipo,
  que es lo que pedía el requisito.

**Decisiones de diseño que conviene no revertir sin leer el motivo:**

1. **`proxied` no es configurable, y de ahí sale todo el diseño del HTTPS.** El
   comodín gratuito de Universal SSL cubre `*.coderhivex.com`, que es UN nivel;
   `airflow.idempotencia.coderhivex.com` tiene DOS. Quien emite el certificado para ese
   nombre es **Total TLS** (parte de Advanced Certificate Manager, ~10 USD/mes),
   y Total TLS solo actúa sobre hostnames **proxeados**. Exponer `proxied` al
   usuario sería darle un botón para romper su propio HTTPS sin entender por qué.
   Efecto colateral asumido: estos subdominios solo enrutan HTTP/HTTPS, no
   puertos TCP arbitrarios.

2. **Se rechazan las IPs no públicas** ([`DTOs/IpAddressRules.cs`](../DTOs/IpAddressRules.cs)).
   Con el registro proxeado, quien se conecta al origen es el borde de Cloudflare
   desde internet: una IP privada, de loopback o de CGNAT es una IPv4 válida y
   perfectamente inalcanzable desde ahí. Sin esta validación el subdominio se
   crearía sin error y fallaría después, en el navegador de quien lo visite, con
   un 522 que no explica nada. **Ojo:** `Provisioning:IpVps` (`100.99.206.50`)
   cae en el rango CGNAT de Tailscale y **no pasa** esta validación — si alguna
   vez se quiere que los usuarios apunten a la propia plataforma, hace falta una
   IP pública de verdad.

3. **El parseo de la IP exige la forma canónica de cuatro octetos decimales.**
   `IPAddress.TryParse` acepta notación hexadecimal, octal y enteros de 32 bits
   (`2130706433` resuelve a `127.0.0.1`), así que sin esa comprobación previa el
   filtro de rangos privados se esquivaría escribiendo la misma IP de otra forma.

4. **`Deleted` y `Revoked` son estados distintos.** Los dos liberan el nombre,
   pero solo así una auditoría puede distinguir lo que el usuario dio de baja de
   lo que el equipo le quitó. Con `RevokedByUserId`/`RevokedAt`/`RevokeReason` y
   una constraint que exige las tres juntas, no existe el "revocado por nadie,
   sin motivo".

5. **La administración vive en un controller aparte**, no como rutas extra con
   `[Authorize(Roles)]`. Así la autorización se declara una vez a nivel de clase
   —no se puede agregar un endpoint mañana y olvidar el atributo, que es el error
   que expone datos de todos los usuarios— y el contrato queda separado: las
   respuestas de admin incluyen el dueño de cada registro, dato que en el
   controller del usuario no debe aparecer nunca. Por el mismo motivo hay dos SPs
   de detalle (`sp_GetDnsRecordDetail` y `sp_GetDnsRecordDetailAdmin`) en vez de
   uno con `@UserId` opcional: un parámetro que significa "no filtres" convierte
   un olvido en una fuga.

6. **La unicidad la garantiza un índice único filtrado**
   (`WHERE Status IN ('Provisioning','Active')`), no un `UNIQUE` plano. Con un
   UNIQUE plano, el primero que creara y borrara `airflow.datos` bloquearía ese
   nombre para siempre. La comprobación previa del SP existe solo para dar un
   mensaje entendible en vez de un 2601 → 500; la carrera real la resuelve el
   índice.

7. **Reconciliación de registros huérfanos.** Si Cloudflare crea el registro pero
   `sp_ConfirmDnsRecord` falla, el catálogo se queda sin `ProviderRecordId` y el
   registro quedaría resolviendo para siempre, bloqueando ese nombre sin que
   nadie pueda liberarlo. Por eso `IDnsProvider` expone `FindRecordIdAsync(fqdn)`,
   que usan la reversión, el borrado y la revocación como respaldo.

8. **Los códigos 7000/7003 de Cloudflare NO se tratan como "no existe"**, aunque
   se parezcan: aparecen cuando el `ZoneId` está mal configurado. Tratarlos como
   "ya borrado" convertiría un despliegue roto en un borrado silencioso y
   exitoso — el catálogo se limpiaría mientras los registros reales siguen vivos.

9. **No hay factory de proveedores de DNS**, a diferencia de
   `IDatabaseProvisionerFactory`. Los motores de BD son cuatro y conviven; el
   proveedor de DNS es uno solo por despliegue. La interfaz existe igual, para
   que agregar Route53 no obligue a tocar el orquestador.

**Deuda conocida, asumida a propósito:** la célula **no valida pertenencia**. No
existe catálogo de células ni relación usuario↔célula en la base, así que
cualquier usuario autenticado puede crear un subdominio bajo el nombre de
cualquier célula. El control mientras tanto es a posteriori (`GET /admin/dns?cell=`
y la revocación). Cuando exista el catálogo, el cambio es una tabla más y una
validación dentro de `sp_ReserveDnsRecord`: **nada del backend cambia**, porque la
célula ya viaja en el request y ya se persiste en su propia columna.

**Qué quedó pendiente:**

- **No se pudo compilar ni verificar en vivo.** El entorno de esta sesión no
  tiene salida a `api.cloudflare.com` (el `curl` de verificación del token da 403
  en el proxy) ni a `api.nuget.org` / los servidores de .NET, así que no hay SDK
  para correr `dotnet build`. **El código está sin compilar.**
- **Contratar ACM y activar Total TLS** en la zona `coderhivex.com`. Sin eso los
  subdominios resuelven pero dan error de certificado, y el requisito de HTTPS no
  se cumple. El `curl` está en `docs/cambios-db-dns-2026-08-12.md` §7.
- Ejecutar `sql/2026-08-12-dns-records.sql` y confirmar las dos dependencias de
  esquema que el script detecta y avisa por `PRINT`: la FK `FK_DnsRecords_Users`
  y la columna `dbo.Users(Email)` del listado administrativo.
- **Verificar el ZoneId.** El identificador `c1c62663d28fa916dc9bc030103e6e83` se
  entregó cuando la conversación todavía hablaba de otro dominio; hay que
  confirmar que sea el de `coderhivex.com` y no el de la zona anterior.
- El token está hoy en `appsettings.json` junto al resto de los secretos (ítem 3
  del backlog). Pasarlo a `Dns__ApiToken` por entorno en el despliegue.

**Ajuste posterior (misma sesión):** el usuario confirmó que su célula es
`idempotencia`, así que `cell` pasó a ser **opcional** en `POST /dns` y cae a
`Dns:DefaultCell` (`idempotencia`) cuando no viene. El campo sigue existiendo en
el contrato para el día que haya varias células: hacer que el frontend repita en
cada request un valor que ya vive en la configuración del backend es exactamente
el tipo de dato que después queda desincronizado. `GET /dns/zone` ahora devuelve
también `defaultCell` y un `pattern` ya resuelto
(`{label}.idempotencia.coderhivex.com`), para que la vista previa del frontend no
tenga que componer nada. La base **no** cambió: el SP sigue recibiendo la célula
explícita, así que ya soporta varias.

**Segundo ajuste (misma sesión):** la cuota bajó de 5 a **3 subdominios vivos por
usuario**, a pedido del usuario. Vive solo en `@MaxRecordsPerUser` de
`sp_ReserveDnsRecord`, deliberadamente sin contraparte en el backend: una cuota
validada en dos lados termina diciendo cosas distintas y gana la más restrictiva
sin que nadie entienda por qué. Cambiarla de nuevo es un `CREATE OR ALTER` de ese
SP — sin migración de datos ni redespliegue.

**Documentos actualizados:** `docs/routes.md` (9 rutas nuevas, hallazgo 13,
total 13 → 22), `docs/API.md` (secciones 11 y 12 nuevas + filas en la tabla de
estado), `docusaurus-docs/08-dns-subdominios.md` (nuevo),
`docusaurus-docs/guia-frontend-subdominios.md` (nueva guía de consumo para el
frontend, en la línea de las dos guías de bases de datos),
`docs/cambios-db-dns-2026-08-12.md` (nuevo), `idempotencia.http` y este archivo.

---

## Sesión 18 — 2026-08-12 (MongoDB pasa a aprovisionarse contra la API externa del equipo)

**Pedido original:** el equipo entregó una API de aprovisionamiento de MongoDB
(`https://mongo.szapatar.dev`, API key de tipo `team` para el equipo
`Idempotencia`) con su contrato de endpoints, y se pidió integrarla **sin
cambiar los endpoints ya creados** de este backend.

**El problema de fondo:** esa API no es un reemplazo pieza-por-pieza del
`MongoProvisioner` local. Choca con tres supuestos que el orquestador daba por
sentados desde el día uno:

1. **El nombre físico lo decide ella.** `POST /databases` genera un nombre
   aleatorio, independiente del `username` que se le manda, así que el nombre
   que reserva `sp_ReserveDatabase` deja de ser el nombre real de la base.
2. **La contraseña la decide ella.** La genera y la devuelve; no acepta una
   impuesta. `PasswordGenerator` deja de mandar en este motor.
3. **Se direcciona por `id`, no por nombre.** Borrar y rotar credenciales son
   `DELETE /databases/{id}` y `POST /databases/{id}/credentials/reset`; sin
   guardar ese id, una base creada por la API queda imposible de eliminar desde
   Colmena.

Y no ofrece dos cosas que los endpoints existentes sí prometen: **desactivar/
reactivar** y **tamaño por base**.

**Decisiones tomadas (confirmadas con el usuario antes de escribir código):**

- **Reemplazar, no convivir.** `Engine = "Mongo"` sigue siendo el mismo valor;
  no se agregó un motor nuevo ni se tocó el regex de `CreateDatabaseRequest`.
  Cero cambios de ruta, método, auth o rate limit.
- **Persistir la referencia externa en el catálogo** (columnas nuevas + SPs),
  en vez de resolver el id llamando a `GET /teams/{team}/databases` en cada
  operación. El listado quedó igual como plan B.
- **Emular desactivar/reactivar rotando credenciales.** Desactivar rota y
  **descarta** la contraseña nueva: nadie la conoce, la credencial vieja muere,
  la base queda inalcanzable y los datos intactos. Reactivar rota otra vez y
  **sí** entrega la nueva, por correo. `GetSizeMbAsync` devuelve `-1`
  ("no medible") y el job conserva el último valor conocido.

**Qué se hizo:**

- [`Provisioners/RemoteMongoProvisioner.cs`](../Provisioners/RemoteMongoProvisioner.cs)
  — nuevo. `HttpClient` tipado contra la API, con `X-API-Key`. Mismo rol
  arquitectónico que `CloudflareDnsProvider`: adaptador hacia un sistema
  externo. Incluye el fallback que resuelve el id por
  `GET /teams/{team}/databases` cuando el catálogo no lo tiene.
- [`Interfaces/IDatabaseProvisioner.cs`](../Interfaces/IDatabaseProvisioner.cs)
  — el contrato crece en dos puntos, ambos no-ops para los motores locales:
  los cuatro métodos de ciclo de vida reciben `externalId`, y las dos
  rotaciones de credencial devuelven `CredentialRotationResult?` (`null` =
  "apliqué la contraseña que me pasaste"). Se documentó además la convención
  del negativo en `GetSizeMbAsync`.
- [`Models/ProvisionResult.cs`](../Models/ProvisionResult.cs) — campos
  opcionales nuevos (`ExternalId`, `EffectiveDbName`, `EffectivePassword`,
  `ConnectionUri`) para que un provisioner pueda reportar lo que REALMENTE
  quedó creado cuando no coincide con lo que se le pidió. Los cuatro locales
  siguen construyéndolo con dos argumentos.
- [`Models/CredentialRotationResult.cs`](../Models/CredentialRotationResult.cs) y
  [`Models/ExternalDatabaseRef.cs`](../Models/ExternalDatabaseRef.cs) — nuevos.
- [`Services/DatabaseProvisioningService.cs`](../Services/DatabaseProvisioningService.cs)
  — usa los valores efectivos al crear, persiste la referencia externa **antes**
  de confirmar (si esa escritura falla, la reversión todavía tiene el id en
  memoria para borrar la base del otro lado), y lee la referencia en todo el
  ciclo de vida. La lógica de "hashear + notificar una contraseña rotada" se
  extrajo a un método compartido por el reset y la reactivación.
- [`Services/RemoteMongoSettings.cs`](../Services/RemoteMongoSettings.cs) —
  nuevo, sección `Provisioning:Mongo:Remote`. `Program.cs` valida al arrancar
  que si `Enabled` está en `true` haya `ApiKey`, mismo criterio que
  `Provisioning:IpVps` y `Dns:ApiToken`.
- [`Program.cs`](../Program.cs) — registra **exactamente uno** de los dos
  provisioners de Mongo según `Provisioning:Mongo:Remote:Enabled`. El factory
  resuelve por `Engine`, así que registrar ambos dejaría la elección al orden
  de la colección de DI.
- [`Provisioners/MongoProvisioner.cs`](../Provisioners/MongoProvisioner.cs) —
  se conserva y se mantiene compilando. Es el camino de vuelta (`Enabled=false`)
  y la única forma de seguir operando las bases de Mongo creadas **antes** de
  la migración, que viven en el servidor propio y no existen en la API externa.
- [`Services/DatabaseSizeMonitor.cs`](../Services/DatabaseSizeMonitor.cs) — salta
  las mediciones negativas y las cuenta aparte de los fallos (`noMedibles`): no
  hay nada que reintentar, es una propiedad del motor.
- [`Services/EmailTemplates.cs`](../Services/EmailTemplates.cs) — plantilla nueva
  `DatabaseReactivatedCredentials`, para el único caso en que reactivar entrega
  una contraseña nueva.
- [`sql/2026-08-12-mongo-external-ref.sql`](../sql/2026-08-12-mongo-external-ref.sql)
  — nuevo. Agrega `ExternalId`/`ExternalDbName` a `ProvisionedDatabases` y crea
  `sp_SetDatabaseExternalRef` + `sp_GetDatabaseExternalRef`. **No toca ningún SP
  existente** — se eligió un SP de lectura aparte justamente para no reescribir
  `sp_GetDatabaseDetail`, cuya definición no está versionada en el repo.
  Idempotente.

**Cambio de comportamiento visible para el frontend** (la forma de las
respuestas NO cambia, el contenido sí):

- `POST /databases` con `engine: "Mongo"` devuelve en `dbName` el nombre que
  generó la API externa, no el del catálogo. `GET /databases/{id}` reporta el
  mismo. Es el nombre al que el usuario realmente se conecta.
- `POST /databases/{id}/reactivate` con Mongo **envía un correo con una
  contraseña nueva**; en los otros tres motores sigue sin mandar nada porque la
  contraseña de siempre vuelve a funcionar. La respuesta HTTP es idéntica en
  ambos casos y nunca incluye la contraseña.
- `currentSizeMB` de las bases Mongo se congela en su último valor conocido.

**Pendiente al cierre (nada de esto se pudo hacer en esta sesión):**

1. **No se compiló.** El entorno de esta sesión no tiene el SDK de .NET y no
   pudo instalarlo (proxy). El cambio se revisó estáticamente miembro por
   miembro contra la interfaz, pero falta un `dotnet build` real.
2. **No se probó contra la API.** `mongo.szapatar.dev` no pasa el proxy de este
   entorno, así que el contrato se implementó tal como está escrito en el
   documento del equipo. El punto más frágil es el formato exacto de
   `connectionString` (de ahí salen host y puerto): se parsea con `MongoUrl` y
   se cae a `PublicHost`/`PublicPort` si no se entiende.
3. **Falta ejecutar `sql/2026-08-12-mongo-external-ref.sql`** contra la BD real
   antes de desplegar. Sin ese script, toda creación de Mongo falla al intentar
   guardar la referencia externa (y revierte limpio, pero falla).
4. **Mover la API key a variable de entorno** (`Provisioning__Mongo__Remote__ApiKey`)
   en vez de dejarla en `appsettings.json`, mismo criterio que el resto de
   secretos del proyecto (`bugs.md` ítem 3).

**Documentos actualizados:** `docs/routes.md` (nota de revisión — sin cambios de
rutas), `docs/API.md` (cambio de comportamiento de reactivate y de `dbName` en
Mongo) y este archivo.

---

## Backlog / próximos pasos

1. **Redesplegar el backend actual a QA** para que lleguen los fixes ya
   committeados que el front todavía ve fallar contra el desplegado
   (validación de email de 150 caracteres — ítem 20; callback OAuth por
   redirect; rate limit OAuth — ítem 2; auto-aprovisionamiento OAuth + correo —
   ítem 16, sesión 13) y **setear `Frontend:BaseUrl` al
   dominio real** (`https://idempotencia.andrescortes.dev`) en el appsettings
   del ambiente desplegado, no `localhost`. En el mismo paso, **setear
   `Provisioning:IpVps`** a la IP pública del VPS (ítem 27, sesión 15): sin esa
   clave el backend ya no arranca, y con ella mal puesta el usuario recibe un
   host al que no puede conectarse. Revisar también que
   `Provisioning:{Engine}:Port` sean los puertos publicados hacia afuera, no los
   internos del contenedor.
2. **Confirmar la compilación tras los fixes de la sesión 11**: `dotnet build`
   (ítems 2/5/7) y, para el ítem 4, `dotnet restore` +
   `dotnet list package --vulnerable` para confirmar que `NU1903` desaparece
   con el pin de `Microsoft.OpenApi` 2.7.5 (mover el ítem 4 de 🟡 a 🟢). Incluye
   también los cambios de la sesión 13 (`AuthService`/`EmailTemplates`) y los de
   la sesión 15 (`ProvisioningSettings` + los cuatro provisioners), y hacer
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
7. ~~**Endpoint de "reactivar" una BD desactivada**~~ — **resuelto en la
   sesión 14** (`POST /databases/{id}/reactivate`, ítem 26). Falta ejecutar
   `sql/2026-07-29-reactivate.sql` y desplegar.
8. **Aplicar la cuota de almacenamiento** en Postgres/MySQL/Mongo — ítem 10.
   La MEDICIÓN ya está resuelta por el `DatabaseSizeMonitor` de la sesión 14
   (ítem 25); lo que falta es actuar sobre ella: revocar escrituras al superar
   `MaxStorageMB` y restaurarlas al volver a estar por debajo. Implica un
   estado nuevo (`'OverQuota'`) en `CK_ProvDb_Status` y su SP.
9. **Ejecutar en la BD de QA los tres scripts pendientes de la sesión 14, en
   este orden:** `sql/2026-07-29-fix-ck-provdb-status.sql` (ítem 24, desbloquea
   desactivar — va primero), `sql/2026-07-29-reactivate.sql` (ítem 26) y
   `sql/2026-07-29-size-sync.sql` (ítem 25, sin él el job no tiene SPs que
   llamar). El primero incluye un paso que verifica si algún otro SP tiene su
   guarda escrita contra `'Paused'`.
10. **Límite de conexiones concurrentes en SQL Server** vía logon trigger —
    ítem 12.
10b. **Habilitar TLS en los contenedores de Postgres y Mongo** y prender
    `Provisioning:Postgres:RequireTls` / `Provisioning:Mongo:RequireTls` (ítem
    28, sesión 16). Hoy las credenciales de esos dos motores viajan sin cifrar
    por el puerto público; MySQL y SqlServer ya quedaron cubiertos. Incluye
    correr el backfill `sql/2026-07-30-mysql-require-ssl.sql` en MySQL para los
    usuarios creados antes del cambio.
11. **Token JWT (y PII) en query string del redirect OAuth** — ítems 1 y 15
    (mismo fix: intercambio por código de un solo uso).
12. **Secretos reales en texto plano en `appsettings.json`** (incluye SMTP,
    las credenciales del `sa` y ahora también `Dns:ApiToken`) — ítem 3. El
    servicio de DNS ya soporta pasarlo por entorno (`Dns__ApiToken`); falta
    hacerlo en el despliegue.
13. **Compilar y verificar el servicio de DNS de la sesión 17.** El código está
    escrito pero **sin compilar**: el entorno de esa sesión no tenía SDK de .NET
    ni salida a NuGet. Correr `dotnet build` antes de cualquier despliegue.
14. **Contratar Advanced Certificate Manager y activar Total TLS** en la zona
    `coderhivex.com`. Es la dependencia que hace que el requisito de HTTPS se
    cumpla: el comodín gratuito cubre un solo nivel y estos subdominios tienen
    dos, así que sin ACM resuelven pero dan error de certificado. El `curl` está
    en [`docs/cambios-db-dns-2026-08-12.md`](cambios-db-dns-2026-08-12.md) §7.
15. **Desplegar `sql/2026-08-12-dns-records.sql`** en el catálogo (10 SPs +
    2 tablas + 4 índices) y revisar los dos avisos por `PRINT` que el script
    puede emitir: la FK `FK_DnsRecords_Users` y la columna `dbo.Users(Email)`
    del listado administrativo. Si se llegó a ejecutar la primera versión del
    script (la de `{label}.idempotencia.<zona>`), seguir antes el apartado
    "MIGRAR DESDE LA v1" del archivo.
16. **Verificar el ZoneId y el token de Cloudflare en vivo.** El identificador
    `c1c62663d28fa916dc9bc030103e6e83` se entregó cuando la conversación todavía
    hablaba de otro dominio: hay que confirmar que sea el de `coderhivex.com`.
17. **Catálogo de células.** Hoy la célula es texto validado por formato y
    cualquier usuario autenticado puede crear un subdominio bajo el nombre de
    cualquier célula (sesión 17, deuda asumida). Cuando exista la tabla de
    células y la relación con `Users`, agregar la validación de pertenencia en
    `sp_ReserveDnsRecord` — el backend no necesita cambios.

**Resueltos/cerrados en la sesión 12 (revisión del front):** callback OAuth por
redirect y validación de email 150 ya estaban en el código (falta redeploy a
QA); connection string a master aceptado como deuda técnica (ítem 22); GitHub
SSO documentado como comportamiento esperado (ítem 23).

11. **Compilar y probar la integración con la API externa de MongoDB**
    (sesión 18): `dotnet build`, y luego un ciclo completo en vivo contra
    `https://mongo.szapatar.dev` — crear una BD Mongo, conectarse con la
    `connectionUri` devuelta, desactivar, reactivar (confirmar que llega el
    correo con la contraseña nueva) y eliminar. Es el único paso que puede
    confirmar el formato real de `connectionString` (de donde salen host y
    puerto) y que la emulación de desactivar/reactivar se comporta como se
    espera. Ninguna de las dos cosas se pudo verificar en la sesión que
    escribió el código: sin SDK de .NET y sin salida de red hacia ese dominio.
12. **Ejecutar `sql/2026-08-12-mongo-external-ref.sql`** en la BD real antes de
    desplegar la sesión 18. Sin ese script toda creación de Mongo falla al
    guardar la referencia externa. Idempotente, se puede correr varias veces.
13. **Mover `Provisioning:Mongo:Remote:ApiKey` a variable de entorno**
    (`Provisioning__Mongo__Remote__ApiKey`), igual que el resto de secretos
    (`bugs.md` ítem 3). Hoy quedó en `appsettings.json` junto a los demás.
14. **Decidir el destino de las bases Mongo anteriores a la migración.** Viven
    en el servidor Mongo propio y no existen en la API externa: con
    `Provisioning:Mongo:Remote:Enabled=true` sus operaciones de ciclo de vida
    fallan con un 409 explicativo. Hay que migrarlas o darlas de baja; cuando no
    quede ninguna se puede borrar `Provisioners/MongoProvisioner.cs` y
    `Provisioning:Mongo:AdminConnectionString`.
