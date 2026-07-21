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
| POST | `/auth/register` | Anónimo | `auth` (10/min/IP) | ⚠️ Código completo; depende de `sp_RegisterUser` | Hashea password con BCrypt en backend, el SP solo persiste. |
| POST | `/auth/login` | Anónimo | `auth` (10/min/IP) | ⚠️ Código completo; depende de `sp_GetLoginByEmail` | Probado en vivo: pipeline HTTP y manejo de errores confirmados end-to-end. |
| GET | `/auth/google/login` | Anónimo | Solo global (100/min/IP) | ✅ Funcional | Redirige (`Challenge`) al flujo OAuth de Google. Credenciales configuradas en `appsettings.json`. |
| GET | `/auth/google/callback` | Anónimo (cookie `External`) | Solo global | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` | Ver hallazgo sobre token en query string en `bugs.md`. |
| GET | `/auth/github/login` | Anónimo | Solo global (100/min/IP) | ✅ Funcional | Redirige al flujo OAuth de GitHub. |
| GET | `/auth/github/callback` | Anónimo (cookie `External`) | Solo global | ⚠️ Código completo; depende de `sp_UpsertExternalLogin` | Igual que Google callback. |
| POST | `/databases` (engine=`SqlServer`) | JWT Bearer | Solo global | ⚠️ Código completo; depende de `sp_ReserveDatabase` / `sp_ConfirmDatabase` / `sp_FailDatabase` | Único motor con provisioner real implementado (`SqlServerProvisioner`). |
| POST | `/databases` (engine=`Postgres`/`MySql`/`Mongo`) | JWT Bearer | Solo global | ❌ Stub — devuelve `501 Not Implemented` | Provisioners son stubs intencionales (`PostgresProvisioner`, `MySqlProvisioner`, `MongoProvisioner`), con instrucciones de implementación en comentarios. |
| GET | `/databases` | JWT Bearer | Solo global | ⚠️ Código completo; depende de `sp_GetUserDatabases` | Lista las BDs del usuario autenticado (claim `UserId`). |

## Rutas de infraestructura (no de negocio)

| Ruta | Entorno | Estado |
|---|---|---|
| `/openapi/v1.json` | Solo Development | ✅ Documento OpenAPI autogenerado. |
| `/swagger` | Solo Development | ✅ Swagger UI. |
| `/scalar` (Scalar API reference) | Solo Development | ✅ Mapeado vía `MapScalarApiReference()`. |

## Total de endpoints de negocio: 8

- 6 en `AuthController` (`/auth/...`)
- 2 en `DatabasesController` (`/databases`)

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

## Mantenimiento de este documento

Cuando se agregue, elimine o modifique una ruta en `Controllers/`, este
archivo (`docs/routes.md`) y `docs/API.md` deben actualizarse en el mismo
cambio. La convención para que esto sea automático con Claude Code está
descrita en `CLAUDE.md` (raíz del proyecto).
