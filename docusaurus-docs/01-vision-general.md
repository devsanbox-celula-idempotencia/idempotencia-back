---
id: vision-general
title: Visión General
sidebar_position: 1
sidebar_label: Visión General
---

# idempotencia-back — API Colmena

Backend de la plataforma **Colmena**, cuyo propósito es permitir que los
estudiantes se registren y obtengan automáticamente bases de datos para sus
proyectos, en cualquiera de cuatro motores soportados.

## Stack tecnológico

| Componente | Tecnología |
|---|---|
| Framework | .NET 10 (ASP.NET Core Web API) |
| ORM / acceso a datos | Entity Framework Core 10 (SQL Server) |
| Autenticación | JWT Bearer (propio) + OAuth 2.0 (Google, GitHub) |
| Hash de contraseñas | BCrypt.Net |
| Documentación de API | OpenAPI + Swagger UI + Scalar (solo en `Development`) |
| Motores de BD soportados para aprovisionar | SQL Server, PostgreSQL, MySQL, MongoDB |

Paquetes clave (`idempotencia.csproj`): `Microsoft.EntityFrameworkCore.SqlServer`,
`Microsoft.AspNetCore.Authentication.Google`, `AspNet.Security.OAuth.GitHub`,
`Microsoft.AspNetCore.Authentication.JwtBearer`, `Npgsql`, `MySqlConnector`,
`MongoDB.Driver`, `BCrypt.Net-Next`, `Scalar.AspNetCore`.

## Arquitectura: database-centric

Toda la **lógica de negocio vive en Stored Procedures** dentro de SQL Server
(catálogo de usuarios y bases de datos). El backend en sí es un **mediador
HTTP**: expone endpoints REST, gestiona JWT/OAuth, valida entrada, invoca los
SPs correspondientes y traduce el resultado (o el error) a una respuesta HTTP
uniforme.

Esto implica una consecuencia importante para cualquiera que dé mantenimiento
al proyecto: **los Stored Procedures no están versionados en este
repositorio** — viven directamente en la base de datos remota. Cambios de
esquema o de contrato de un SP deben coordinarse manualmente contra el
servidor real.

### Capas del proyecto

| Carpeta | Responsabilidad |
|---|---|
| `Controllers/` | Puntos de entrada HTTP (`AuthController`, `DatabasesController`, `StatisticsController`). Sin lógica de negocio — delegan en servicios. |
| `Services/` | Orquestación: `AuthService`, `DatabaseProvisioningService`, `JwtTokenService`, `OAuthRedirectBuilder`, `PasswordGenerator`. |
| `Repository/` | Acceso a datos vía SPs (`FromSqlRaw` + `SqlParameter` tipados) — `UserRepository`, `DatabaseRepository`, `StatisticsRepository`. |
| `Provisioners/` | Un provisioner por motor de base de datos (patrón *Strategy*): `SqlServerProvisioner`, `PostgresProvisioner`, `MySqlProvisioner`, `MongoProvisioner`. |
| `Middleware/` | `ExceptionHandlingMiddleware` (traduce excepciones a JSON uniforme) y las excepciones propias (`AppException`, `AuthException`). |
| `DTOs/` | Contratos de entrada/salida de cada endpoint. |
| `Models/` | Entidades mapeadas desde los SPs (`ProvisionedDatabaseInfo`, `UserIdentity`, `LoginInfo`, etc.). |

### Aprovisionamiento multi-motor (patrón Strategy)

`IDatabaseProvisionerFactory` recibe el motor solicitado (`SqlServer`,
`Postgres`, `MySql`, `Mongo`) y selecciona la implementación de
`IDatabaseProvisioner` correspondiente. Cada provisioner sabe crear un
usuario/rol y una base de datos físicos en su motor, aplicando permisos
acotados solo a esa base (nunca privilegios globales).

## Flujo de una petición típica

```
Cliente → Controller → Service (regla de orquestación)
                     → Repository → Stored Procedure (SQL Server remoto)
                     → Provisioner (si aplica: crea la BD física en el motor pedido)
        ← Middleware de excepciones traduce cualquier error a {status, error}
```

## Dónde seguir leyendo

- [Autenticación](./autenticacion) — JWT, login por contraseña, OAuth Google/GitHub.
- [Referencia de la API](./api-referencia) — todos los endpoints, bodies, respuestas y errores.
- [Aprovisionamiento de bases de datos](./aprovisionamiento-bases-de-datos) — motores, cuotas, límites de conexión.
- [Manejo de errores y rate limiting](./manejo-de-errores)
- [Configuración por ambiente](./configuracion-ambientes) — Development / QA / Producción.
- [Seguridad y pendientes conocidos](./seguridad-y-pendientes)
