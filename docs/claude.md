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

## Backlog / próximos pasos

Pendientes conocidos, de mayor a menor severidad (detalle y solución
propuesta en `docs/bugs.md`):

1. **Confirmar `sp_UpsertExternalLogin`** (callback OAuth Google/GitHub) si
   tiene el mismo bug de `Roles`/`RoleId` que tenía `sp_GetLoginByEmail`
   (ítem 9, ya resuelto) — pedir su `sp_helptext` o probar un login OAuth
   real. `sp_RegisterUser` ya se confirmó SIN el bug.
2. **Ciclo de vida (TTL)**: implementar `sp_GetIdleDatabases`,
   `sp_PauseDatabase`, `sp_DeleteDatabase` + `DatabaseLifecycleJob`
   (`IHostedService`) — ítem 11 de `bugs.md`. Definir primero de dónde sale
   la señal real de `LastActivityAt`.
3. **Cuota de almacenamiento real** para Postgres/MySQL/Mongo —
   `DatabaseQuotaMonitor` (`IHostedService`) — ítem 10 de `bugs.md`.
4. **Límite de conexiones concurrentes en SQL Server** vía logon trigger,
   probado contra una BD de prueba antes de desplegar — ítem 12 de
   `bugs.md`.
5. Token JWT viaja en query string en el redirect OAuth — ítem 1 de
   `bugs.md` (mover a fragmento `#` o a un código de un solo uso).
6. Secretos reales en texto plano en `appsettings.json` — ítem 3 de
   `bugs.md` (mover a User Secrets / vault).
7. Vulnerabilidad conocida en `Microsoft.OpenApi` — ítem 4 de `bugs.md`
   (actualizar el paquete).
8. Sin rate limit dedicado en los 4 endpoints OAuth (`/auth/google/*`,
   `/auth/github/*`) — ítem 2 de `bugs.md`.
9. `CurrentSizeMB` sin tipo de columna explícito en EF Core — ítem 5 de
   `bugs.md` (riesgo de truncamiento silencioso).
10. Verificar en vivo que `sp_ReserveDatabase`, `sp_ConfirmDatabase`,
    `sp_FailDatabase` y `sp_GetPlatformStatistics` devuelven exactamente las
    columnas que esperan sus `Models/*.cs` — `sp_GetLoginByEmail`,
    `sp_RegisterUser` y `sp_GetUserDatabases` ya se verificaron en vivo.

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
