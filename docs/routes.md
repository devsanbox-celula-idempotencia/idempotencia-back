# Rutas del backend — estado escaneado

> Generado analizando el código fuente (`Controllers/`, `Program.cs`) y probando
> el arranque real de la app (`dotnet build` + `dotnet run`) el 2026-07-21.
> **Este documento debe actualizarse cada vez que cambien las rutas** (ver nota
> al final y `CLAUDE.md`).

## Cómo se evaluó "funcional"

- **✅ Funcional**: el código compila, la ruta está mapeada, la lógica está
  completamente implementada y no depende de nada pendiente.
- **⚠️ Funcional con dependencia externa no verificable**: el código está
  completo, pero depende de un Stored Procedure en la base de datos remota
  (`46.224.101.88`) que **no se pudo verificar** desde este entorno (el puerto
  1433 no fue alcanzable al hacer la prueba — timeout de conexión; sí hay
  salida a internet en general, así que puede ser un firewall específico de
  ese host/puerto en este sandbox, no necesariamente un problema del server
  real). Se probó `dotnet run` real y la petición `POST /auth/login` llegó
  hasta el intento de conexión SQL y fue capturada correctamente por el
  middleware de errores (500 uniforme), lo que confirma que el pipeline HTTP
  funciona de punta a punta.
- **❌ No funcional / stub**: el endpoint responde pero la operación de fondo
  está deliberadamente sin implementar (devuelve `501`).

## Tabla de rutas

| Método | Ruta | Auth | Rate limit | Estado | Notas |
|---|---|---|---|---|---|
| POST | `/auth/register` | Anónimo | `auth` (10/min/IP) | ⚠️ Código completo; depende de `sp_RegisterUser` (confirmado en vivo, sin bug de `Roles`) | Hashea password con BCrypt en backend, el SP solo persiste. **Además dispara auto-aprovisionamiento de BD MySQL** (ver hallazgo 5). |
| POST | `/auth/login` | Anónimo | `auth` (10/min/IP) | ✅ Confirmado en vivo | Bug de `sp_GetLoginByEmail` corregido (`bugs.md` ítem 9). Enumeración de cuentas OAuth-only corregida (`bugs.md` ítem 14). **Además dispara auto-aprovisionamiento de BD MySQL** (ver hallazgo 5). |
| GET | `/auth/google/login` | Anónimo | Solo global (100/min/IP) | ✅ Funcional | Redirige (`Challenge`) al flujo OAuth de Google. Credenciales configuradas en `appsettings.json`. |
| GET | `/auth/google/callback` | Anónimo (cookie `External`) | Solo global | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` (sin confirmar en vivo) | Ver hallazgo sobre PII+token en query string en `bugs.md` ítems 1 y 15. **YA NO dispara auto-aprovisionamiento de BD MySQL** (removido, ver hallazgo 9) — el frontend debe pedirla explícitamente. |
| GET | `/auth/github/login` | Anónimo | Solo global (100/min/IP) | ✅ Funcional | Redirige al flujo OAuth de GitHub. |
| GET | `/auth/github/callback` | Anónimo (cookie `External`) | Solo global | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` (sin confirmar en vivo) | Igual que Google callback — sin auto-aprovisionamiento (hallazgo 9). |
| POST | `/databases` (cualquier `engine`) | JWT Bearer | **`db-provisioning` (5/min/usuario)** | ⚠️ Código completo; depende de `sp_ReserveDatabase` / `sp_ConfirmDatabase` / `sp_FailDatabase` | **Los 4 motores (`SqlServer`, `Postgres`, `MySql`, `Mongo`) tienen provisioner real implementado** (ver hallazgo 5 — esta fila corrige la versión anterior de esta tabla, que los describía como stubs). Rate limit dedicado agregado en esta revisión (antes solo el global). |
| GET | `/databases` | JWT Bearer | Solo global | ⚠️ Código completo; depende de `sp_GetUserDatabases` | Lista las BDs del usuario autenticado (claim `UserId`). |
| GET | `/databases/{id}` | JWT Bearer | Solo global | 🆕 Código completo; depende de `sp_GetDatabaseDetail` (nuevo, sin desplegar) | Detalle de una BD puntual (host/puerto/usuario/estado, nunca la contraseña) — para cuando el usuario perdió sus datos de conexión. 404 si no existe o no es del usuario (mismo mensaje para ambos casos, evita enumeración). |
| POST | `/databases/{id}/deactivate` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_DeactivateDatabase` (nuevo, sin desplegar) | Revoca el acceso físico (login/usuario deshabilitado en el motor) sin borrar datos. Requiere que la BD esté `Active`; si no, `400`. Paso obligatorio antes de `DELETE`. |
| DELETE | `/databases/{id}` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_DeleteDatabase` (nuevo, sin desplegar) | Borrado físico real (irreversible) — solo permitido si la BD ya está `Inactive`; si no, `400`. |
| POST | `/databases/{id}/reset-password` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_ResetDatabasePassword` (nuevo, sin desplegar) + SMTP configurado | Genera una contraseña nueva, la aplica en el motor y la envía por correo al usuario — la respuesta HTTP nunca incluye la contraseña. Requiere que la BD esté `Active`. |
| GET | `/statistics` | JWT Bearer + rol `Admin` | Solo global | ⚠️ Código completo; depende de `sp_GetPlatformStatistics` | Requiere token con rol `Admin` (401 sin token/expirado, 403 sin rol). No verificado contra la BD real (ver nota de conectividad). |

## Rutas de infraestructura (no de negocio)

| Ruta | Entorno | Estado |
|---|---|---|
| `/openapi/v1.json` | Solo Development | ✅ Documento OpenAPI autogenerado. |
| `/swagger` | Solo Development | ✅ Swagger UI. |
| `/scalar` (Scalar API reference) | Solo Development | ✅ Mapeado vía `MapScalarApiReference()`. |

## Total de endpoints de negocio: 13

- 6 en `AuthController` (`/auth/...`)
- 6 en `DatabasesController` (`/databases...`) — incluye los 4 nuevos de
  detalle/desactivar/eliminar/reset de contraseña (sesión 7, ver `claude.md`)
- 1 en `StatisticsController` (`/statistics`)

## Hallazgos relevantes para esta tabla

1. **`idempotencia.http` referencia `/weatherforecast/`**, un endpoint de la
   plantilla por defecto de .NET que **no existe** en ningún controller. Es un
   archivo de prueba obsoleto — ver `bugs.md`.
2. El motor de BD por defecto en documentación (`SqlServer`) es el único con
   backend físico real; los otros 3 valores de `engine` aceptados por el DTO
   siempre devuelven `501`. Esto ya estaba correctamente documentado en
   `API.md` y se mantiene.
3. No se pudo ejecutar contra la base de datos real (`46.224.101.88:1433`) por
   timeout de conexión desde este entorno de análisis. Las filas marcadas ⚠️
   deben confirmarse manualmente contra una base con los SP desplegados antes
   de asumir que están 100% operativas en producción.
4. **`StatisticsController` (`GET /statistics`) existía en el código (sin
   commitear) pero no estaba documentado aquí ni en `API.md`.** Se agregó en
   esta revisión, en cumplimiento de la regla de `CLAUDE.md` sobre mantener
   `docs/routes.md` y `docs/API.md` sincronizados con `Controllers/`.
5. **Se implementó el aprovisionamiento automático de BD MySQL en el primer
   login** (requisito de negocio: "al iniciar sesión por primera vez deberá
   crearse automáticamente una base de datos MySQL"). Detalle en
   [`Services/AuthService.cs`](../Services/AuthService.cs)
   (`EnsureMySqlDatabaseAsync`), llamado desde `RegisterAsync`, `LoginAsync` y
   `ExternalLoginAsync`. Es idempotente (usa `sp_GetUserDatabases` para
   detectar si el usuario ya tiene una BD MySQL) y **no bloquea el login si el
   aprovisionamiento falla** (se loguea el error y el campo
   `AuthResponse.mySqlDatabase` queda en `null`; el usuario puede reintentar
   con `POST /databases`). Las credenciales viajan en `AuthResponse.mySqlDatabase`
   solo la vez que se crean — ver `API.md` §3.
6. **Corrección de esta tabla**: la fila anterior de `POST /databases` decía
   que `Postgres`/`MySql`/`Mongo` eran stubs devolviendo `501`. Al revisar el
   código fuente en esta revisión se encontró que **ya no son stubs** —
   `PostgresProvisioner`, `MySqlProvisioner` y `MongoProvisioner` tienen
   implementación real (creación de rol/usuario + BD + grants). No se pudo
   confirmar en vivo contra la BD real (ver nota de conectividad), pero el
   código ya no depende de nada pendiente de implementar.
7. **Nuevo rate limit dedicado en `POST /databases`** (`db-provisioning`,
   5/min por usuario autenticado — antes solo tenía el límite global de
   100/min/IP). Aprovisionar una BD física es costoso (conecta al motor real y
   ejecuta DDL), por lo que amerita un límite propio; se particiona por
   `UserId` del JWT en vez de IP porque el endpoint ya requiere autenticación.
8. **Auditoría de exposición de datos sensibles** (a pedido del usuario):
   se encontraron y corrigieron dos hallazgos — enumeración de cuentas
   OAuth-only en `/auth/login` (`bugs.md` ítem 14) y pérdida silenciosa de la
   contraseña de la BD MySQL auto-aprovisionada en el flujo OAuth (ítem 16).
   Se documentó (sin corregir todavía) que el redirect OAuth expone más PII de
   la que se pensaba (ítem 15, amplía el ítem 1).
9. **`ExternalLoginAsync` (callbacks OAuth) YA NO llama
   `EnsureMySqlDatabaseAsync`** — ver `bugs.md` ítem 16. El auto-aprovisionamiento
   de MySQL en primer login (hallazgo 5) ahora solo aplica a
   `RegisterAsync`/`LoginAsync` (login por contraseña). El frontend debe pedir
   `POST /databases` explícitamente tras un primer login OAuth — ver
   `API.md` §5.4.
10. **`POST /databases` ahora acepta `maxConcurrentConnections` opcional**
    (a pedido del usuario), acotado siempre a un cap por motor — ver `API.md`
    §6.1 y `bugs.md` ítem 12.
11. **Ciclo de vida manual de bases de datos** (a pedido del usuario): se
    agregaron `GET /databases/{id}`, `POST /databases/{id}/deactivate`,
    `DELETE /databases/{id}` y `POST /databases/{id}/reset-password`. Código
    completo (`Controllers/DatabasesController.cs`,
    `Services/DatabaseProvisioningService.cs`, `Interfaces/IDatabaseProvisioner.cs`
    extendido con `Host`/`Port`/`ChangePasswordAsync`/`DeactivateAsync` en los
    4 provisioners), pero **depende de 4 SPs nuevos que todavía no están
    desplegados** en la base real
    ([`sql/2026-07-22_database_lifecycle_sps.sql`](../sql/2026-07-22_database_lifecycle_sps.sql))
    y de la sección `Email` (SMTP) en `appsettings.json`, que hoy tiene
    placeholders — **no funcionará hasta que ambas cosas se configuren**. Ver
    `docs/bugs.md` (nuevo hallazgo) y la bitácora en `docs/claude.md`.

## Mantenimiento de este documento

Cuando se agregue, elimine o modifique una ruta en `Controllers/`, este
archivo (`docs/routes.md`) y `docs/API.md` deben actualizarse en el mismo
cambio. La convención para que esto sea automático con Claude Code está
descrita en `CLAUDE.md` (raíz del proyecto).
