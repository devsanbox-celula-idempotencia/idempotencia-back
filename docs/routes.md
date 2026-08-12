# Rutas del backend — estado escaneado

> Generado analizando el código fuente (`Controllers/`, `Program.cs`) y probando
> el arranque real de la app (`dotnet build` + `dotnet run`) el 2026-07-21.
> Última revisión de sincronía código ↔ docs: **2026-07-27** (sesión 12 —
> sin cambios de rutas ni de atributos de auth/rate-limit respecto al estado
> de la sesión 11; los 13 endpoints y sus metadatos coinciden exactamente con
> `Controllers/`).
> Revisión posterior: **2026-07-30** (sesiones 15 y 16 — tampoco cambian rutas,
> métodos, auth ni rate limits. Sí cambió el *contenido* de las respuestas de
> `POST /databases` y del login: campos nuevos `connectionUri`/`jdbcUrl` y un
> valor distinto en `host`; eso está documentado en `docs/API.md` y en
> `docs/cambios-api-frontend-2026-07-30.md`, que es donde vive el contrato de
> payloads).
> Revisión posterior: **2026-08-12** — se agregaron `DnsController` (6 rutas
> bajo `/dns`) y `AdminDnsController` (3 rutas bajo `/admin/dns`, rol `Admin`),
> más una política de rate limiting nueva (`dns`). Ver hallazgo 13.
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
| GET | `/auth/google/login` | Anónimo | `oauth` (20/min/IP) | ✅ Funcional | Redirige (`Challenge`) al flujo OAuth de Google. Credenciales configuradas en `appsettings.json`. |
| GET | `/auth/google/callback` | Anónimo (cookie `External`) | `oauth` (20/min/IP) | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` (sin confirmar en vivo) | Ver hallazgo sobre PII+token en query string en `bugs.md` ítems 1 y 15. **Auto-aprovisiona la BD MySQL en el primer login y envía las credenciales por correo** (ver hallazgo 9) — el frontend ya no necesita pedirla. |
| GET | `/auth/github/login` | Anónimo | `oauth` (20/min/IP) | ✅ Funcional | Redirige al flujo OAuth de GitHub. |
| GET | `/auth/github/callback` | Anónimo (cookie `External`) | `oauth` (20/min/IP) | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` (sin confirmar en vivo) | Igual que Google callback — auto-aprovisiona la BD MySQL y entrega las credenciales por correo (hallazgo 9). |
| POST | `/databases` (cualquier `engine`) | JWT Bearer | **`db-provisioning` (5/min/usuario)** | ⚠️ Código completo; depende de `sp_ReserveDatabase` / `sp_ConfirmDatabase` / `sp_FailDatabase` | **Los 4 motores (`SqlServer`, `Postgres`, `MySql`, `Mongo`) tienen provisioner real implementado** (ver hallazgo 5 — esta fila corrige la versión anterior de esta tabla, que los describía como stubs). Rate limit dedicado agregado en esta revisión (antes solo el global). |
| GET | `/databases` | JWT Bearer | Solo global | ⚠️ Código completo; depende de `sp_GetUserDatabases` | Lista las BDs del usuario autenticado (claim `UserId`). |
| GET | `/databases/{id}` | JWT Bearer | Solo global | 🆕 Código completo; depende de `sp_GetDatabaseDetail` (nuevo, sin desplegar) | Detalle de una BD puntual (host/puerto/usuario/estado, nunca la contraseña) — para cuando el usuario perdió sus datos de conexión. 404 si no existe o no es del usuario (mismo mensaje para ambos casos, evita enumeración). |
| POST | `/databases/{id}/deactivate` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🔴 **ROTO en QA** — `sp_DeactivateDatabase` escribe `'Inactive'` y `CK_ProvDb_Status` no lo permite (SQL 547). Ver `bugs.md` ítem 24; el fix es el `ALTER TABLE` de `sql/2026-07-29-fix-ck-provdb-status.sql`, pendiente de aplicar | Revoca el acceso físico (login/usuario deshabilitado en el motor) sin borrar datos. Requiere que la BD esté `Active`; si no, `400`. Paso obligatorio antes de `DELETE`. **Ya es reversible** con `/reactivate`. |
| POST | `/databases/{id}/reactivate` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_ReactivateDatabase` (nuevo, en `sql/2026-07-29-reactivate.sql`, sin desplegar) | Restaura el acceso revocado por `/deactivate` y devuelve la BD a `Active`. Requiere que esté `Inactive`; si no, `400`. Los datos y la contraseña no cambian. Idempotente en los 4 motores, así que reintentar es seguro. |
| DELETE | `/databases/{id}` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_DeleteDatabase` (nuevo, sin desplegar) | Borrado físico real (irreversible) — solo permitido si la BD ya está `Inactive`; si no, `400`. |
| POST | `/databases/{id}/reset-password` | JWT Bearer | `db-provisioning` (5/min/usuario) | 🆕 Código completo; depende de `sp_ResetDatabasePassword` (nuevo, sin desplegar) + SMTP configurado | Genera una contraseña nueva, la aplica en el motor y la envía por correo al usuario — la respuesta HTTP nunca incluye la contraseña. Requiere que la BD esté `Active`. |
| GET | `/statistics` | JWT Bearer + rol `Admin` | Solo global | ⚠️ Código completo; depende de `sp_GetPlatformStatistics` | Requiere token con rol `Admin` (401 sin token/expirado, 403 sin rol). No verificado contra la BD real (ver nota de conectividad). |
| POST | `/dns` | JWT Bearer | **`dns` (10/min/usuario)** | 🆕 Código completo; depende de `sp_ReserveDnsRecord` / `sp_ConfirmDnsRecord` / `sp_FailDnsRecord` (nuevos, en `sql/2026-08-12-dns-records.sql`, sin desplegar) + la sección `Dns` de configuración + **ACM/Total TLS contratado en la zona** | Autoservicio: crea `{label}.idempotencia.coderhivex.com` como registro **A proxeado** hacia la IPv4 pública que aporta el usuario. El body es `{ label, ipAddress }`; `cell` es opcional y cae a `Dns:DefaultCell` (`idempotencia`). `409` si el nombre ya está tomado; `400` si el nombre/célula son inválidos o reservados, si la IP no es pública, o si se superó la cuota (5 por usuario). |
| GET | `/dns` | JWT Bearer | Solo global | 🆕 Código completo; depende de `sp_GetUserDnsRecords` (nuevo, sin desplegar) | Lista los subdominios vivos del usuario autenticado. |
| GET | `/dns/{id}` | JWT Bearer | Solo global | 🆕 Código completo; depende de `sp_GetDnsRecordDetail` (nuevo, sin desplegar) | Detalle de un subdominio. 404 si no existe o no es del usuario (mismo mensaje para ambos, evita enumeración). |
| PUT | `/dns/{id}` | JWT Bearer | `dns` (10/min/usuario) | 🆕 Código completo; depende de `sp_UpdateDnsRecord` (nuevo, sin desplegar) | Reapunta el subdominio a otra IPv4 pública. El nombre no se puede cambiar. **No** acepta `proxied` ni `ttl`: los fija la plataforma porque de ellos depende el certificado. Requiere estado `Active`. |
| DELETE | `/dns/{id}` | JWT Bearer | `dns` (10/min/usuario) | 🆕 Código completo; depende de `sp_DeleteDnsRecord` (nuevo, sin desplegar) | Elimina el registro en Cloudflare y lo marca `Deleted` en el catálogo, liberando el nombre. **No** exige desactivar primero (a diferencia de las BDs): no se destruye ningún dato. |
| GET | `/dns/zone` | JWT Bearer | Solo global | 🆕 Código completo; sin dependencias de BD | Devuelve `{ zoneName, pattern }` para que el frontend arme la vista previa del nombre completo sin hardcodear el dominio. |
| GET | `/admin/dns` | JWT Bearer + rol `Admin` | Solo global | 🆕 Código completo; depende de `sp_GetAllDnsRecords` (nuevo, sin desplegar) | **Auditoría**: inventario de TODOS los registros de TODOS los usuarios, con el correo del dueño. Filtros `?cell=`, `?userId=`, `?status=`, `?minDaysSinceUpdate=` (este último sostiene la revocación por inactividad). Sin `status` devuelve solo los vivos. |
| GET | `/admin/dns/{id}` | JWT Bearer + rol `Admin` | Solo global | 🆕 Código completo; depende de `sp_GetDnsRecordDetailAdmin` (nuevo, sin desplegar) | Detalle sin filtro de propiedad; incluye los estados terminales (`Deleted`/`Revoked`). |
| POST | `/admin/dns/{id}/revoke` | JWT Bearer + rol `Admin` | `dns` (10/min/usuario) | 🆕 Código completo; depende de `sp_RevokeDnsRecord` (nuevo, sin desplegar) | **Revocación**: elimina el registro en Cloudflare y lo marca `Revoked` con quién/cuándo/por qué. Motivo obligatorio (mín. 10 caracteres) en el body — por eso es POST y no DELETE. |

## Rutas de infraestructura (no de negocio)

| Ruta | Entorno | Estado |
|---|---|---|
| `/openapi/v1.json` | Solo Development | ✅ Documento OpenAPI autogenerado. |
| `/swagger` | Solo Development | ✅ Swagger UI. |
| `/scalar` (Scalar API reference) | Solo Development | ✅ Mapeado vía `MapScalarApiReference()`. |

## Total de endpoints de negocio: 22

- 6 en `AuthController` (`/auth/...`)
- 6 en `DatabasesController` (`/databases...`) — incluye los 4 nuevos de
  detalle/desactivar/eliminar/reset de contraseña (sesión 7, ver `claude.md`)
- 1 en `StatisticsController` (`/statistics`)
- 6 en `DnsController` (`/dns...`) — autoservicio de subdominios sobre Cloudflare
  (2026-08-12, ver hallazgo 13)
- 3 en `AdminDnsController` (`/admin/dns...`) — auditoría y revocación, rol
  `Admin` (2026-08-12, ver hallazgo 13)

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
9. **`ExternalLoginAsync` (callbacks OAuth) auto-aprovisiona la BD MySQL y
   entrega las credenciales por correo** (actualizado 2026-07-23, `bugs.md`
   ítem 16). El auto-aprovisionamiento de MySQL en primer login (hallazgo 5)
   aplica a los tres flujos: `RegisterAsync`/`LoginAsync` entregan las
   credenciales en el `AuthResponse` (JSON); `ExternalLoginAsync` las envía por
   **correo**, porque su respuesta viaja por redirect/query string y ahí no se
   puede entregar un secreto de forma segura. El frontend ya no necesita pedir
   `POST /databases` tras un login OAuth para la BD "principal" — ver `API.md`
   §5.4.
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
    **Actualización 2026-07-23:** ese archivo `sql/...sps.sql` no está
    presente en el repositorio conectado (no existe carpeta `sql/`) — ver el
    detalle en `docs/bugs.md` ítem 19 (actualización 2026-07-23).
    **Actualización 2026-07-23 (v2):** la otra dependencia pendiente de esta
    fila (SMTP en la sección `Email` de `appsettings.json`) ya se resolvió —
    el archivo conectado tiene una cuenta SMTP real de Gmail configurada, ya
    no placeholders. Solo sigue faltando desplegar los 4 SPs.
    **Actualización 2026-07-23 (v3):** el usuario confirmó que los 4 SPs ya se
    desplegaron contra la BD real y que los 4 endpoints funcionan de punta a
    punta — este ítem (bug 19) queda 🟢 Resuelto. También confirmó el fix del
    `redirect_uri_mismatch` de OAuth en QA (bug 17, 🟢).

12. **Nuevo rate limit dedicado en los 4 endpoints OAuth** (`bugs.md` ítem 2,
    2026-07-23): antes solo los cubría el límite global (100/min/IP). Se agregó
    la política `oauth` (20/min por IP) en [`Program.cs`](../Program.cs) y
    `[EnableRateLimiting("oauth")]` en los login/callback de Google y GitHub de
    [`Controllers/AuthController.cs`](../Controllers/AuthController.cs). Se
    reflejó en la columna "Rate limit" de la tabla de arriba.

13. **Autoservicio de subdominios DNS** (`DnsController` + `AdminDnsController`,
    2026-08-12, a pedido del usuario): los usuarios crean subdominios propios
    para sus proyectos bajo `{label}.idempotencia.coderhivex.com` — p. ej.
    `airflow.idempotencia.coderhivex.com` — como registros **A proxeados** hacia la IPv4
    pública de su servicio, vía la API v4 de Cloudflare.

    Sigue el mismo patrón que el aprovisionamiento de bases de datos: controller
    → `IDnsProvisioningService` → (`IDnsRepository` para el catálogo +
    `IDnsProvider` para el sistema externo), con el flujo reservar → crear →
    confirmar / revertir. Diferencias deliberadas respecto de las BDs:

    - **No hay factory de proveedores.** Los motores de BD son cuatro y conviven
      (el usuario elige uno por base); el proveedor de DNS es uno solo por
      despliegue. La interfaz `IDnsProvider` sí existe para que agregar Route53
      no obligue a tocar el orquestador.
    - **Borrar no exige desactivar primero.** En una BD ese paso protege datos
      del usuario; acá no hay datos que proteger y el mismo nombre se puede
      volver a pedir.
    - **Rate limit `dns`** (10/min por usuario). El abuso relevante no es "este
      usuario se hace daño a sí mismo" sino que agote la cuota de la API de
      Cloudflare, **compartida por toda la plataforma**.
    - **La administración vive en un controller aparte**, no como rutas extra con
      `[Authorize(Roles)]` encima. Así la autorización queda declarada una vez a
      nivel de clase (no se puede agregar un endpoint y olvidar el atributo) y el
      contrato queda separado: las respuestas de admin incluyen a qué usuario
      pertenece cada registro, dato que en el controller del usuario no aparece
      nunca.

    **Dependencia que no es código:** el HTTPS exige **Advanced Certificate
    Manager con Total TLS activado** en la zona. El comodín gratuito de Universal
    SSL cubre `*.coderhivex.com` — un solo nivel — y estos nombres tienen dos;
    sin ACM los subdominios resuelven pero dan error de certificado. De ahí que
    `proxied` no sea configurable por el usuario: Total TLS solo emite
    certificados para hostnames proxeados.

    Depende de 10 SPs nuevos (`sql/2026-08-12-dns-records.sql`) y de la sección
    `Dns` de configuración, **ninguno desplegado todavía**. Cambios de base de
    datos en [`docs/cambios-db-dns-2026-08-12.md`](cambios-db-dns-2026-08-12.md);
    flujo de usuario y procedimiento de revocación en
    `docusaurus-docs/08-dns-subdominios.md`. `Program.cs` valida
    `Dns:ZoneId`/`ApiToken`/`ZoneName` al arrancar y no levanta si falta alguna.

    **Deuda conocida:** la célula no valida pertenencia (no existe catálogo de
    células en la base), así que cualquier usuario autenticado puede crear bajo
    el nombre de cualquier célula. El control mientras tanto es a posteriori, con
    `/admin/dns`.

## Mantenimiento de este documento

Cuando se agregue, elimine o modifique una ruta en `Controllers/`, este
archivo (`docs/routes.md`) y `docs/API.md` deben actualizarse en el mismo
cambio. La convención para que esto sea automático con Claude Code está
descrita en `CLAUDE.md` (raíz del proyecto).
