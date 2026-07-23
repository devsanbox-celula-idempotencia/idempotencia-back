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

## Backlog / próximos pasos

1. **Desplegar `sql/2026-07-22_database_lifecycle_sps.sql`** contra la BD
   real (`sp_GetDatabaseDetail`, `sp_DeactivateDatabase`, `sp_DeleteDatabase`,
   `sp_ResetDatabasePassword`) y confirmar que `ProvisionedDatabases` tiene
   columna `LoginName` (ver notas del script) — sin esto los 4 endpoints
   nuevos de la sesión 7 devuelven `500`.
2. **Configurar la sección `Email` de `appsettings.json`** con una cuenta
   SMTP real (App Password de Google u otro proveedor) para que
   `POST /databases/{id}/reset-password` pueda enviar correos — hoy tiene
   placeholders. Idealmente vía User Secrets/variables de entorno.
3. **Confirmar en vivo los 4 endpoints nuevos** (detalle, desactivar,
   eliminar, reset de contraseña) tras los dos pasos anteriores — ítem 19 de
   `bugs.md`, mover de 🟡 a 🟢.
4. **Confirmar en vivo el fix del ítem 17** (`redirect_uri_mismatch` en QA)
   tras desplegar — mover de 🟡 a 🟢 en `bugs.md`.
5. **Confirmar en vivo el fix de `sp_UpsertExternalLogin`** (ítem 9) tras
   aplicar el `ALTER PROCEDURE` en la BD real — probar un login OAuth
   completo de punta a punta.
6. **Evaluar bajar el `MaxLength` de `CreateDatabaseRequest.DbName`** (hoy
   128) para dejar margen bajo el límite de identificador de 64 caracteres
   de MySQL una vez concatenado el prefijo por usuario.
7. **Ciclo de vida automático (TTL)**: implementar `sp_GetIdleDatabases` +
   `DatabaseLifecycleJob` (`IHostedService`) que detecte inactividad y
   reutilice `sp_DeactivateDatabase`/`sp_DeleteDatabase` (ya existen desde la
   sesión 7) en vez de crear SPs nuevas con otro nombre — ítem 11 de
   `bugs.md`, actualizado para no duplicar los SPs manuales.
8. **Endpoint de "reactivar" una BD desactivada** (`ENABLE`/`ACCOUNT UNLOCK`/
   `LOGIN` según el motor) — no se pidió en la sesión 7, hoy desactivar es
   unidireccional hacia eliminar.
9. **Cuota de almacenamiento real** para Postgres/MySQL/Mongo — ítem 10 de
   `bugs.md`.
10. **Límite de conexiones concurrentes en SQL Server** vía logon trigger —
    ítem 12 de `bugs.md`.
11. Token JWT (y PII) viaja en query string en el redirect OAuth — ítems 1 y
    15 de `bugs.md`.
12. Secretos reales en texto plano en `appsettings.json` — ítem 3 de
    `bugs.md`.
13. Vulnerabilidad conocida en `Microsoft.OpenApi` — ítem 4 de `bugs.md`.
14. Sin rate limit dedicado en los 4 endpoints OAuth — ítem 2 de `bugs.md`.
15. `CurrentSizeMB` sin tipo de columna explícito en EF Core — ítem 5 de
    `bugs.md`.
