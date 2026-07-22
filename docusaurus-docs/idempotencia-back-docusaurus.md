# idempotencia-back — API Colmena

> Documentación técnica del backend. Pensada para copiar/adaptar directo a
> Docusaurus (o cualquier generador de sitios de documentación).

## Índice

- [Visión general](#visión-general)
- [Autenticación](#autenticación)
- [Referencia de la API](#referencia-de-la-api)
- [Aprovisionamiento de bases de datos](#aprovisionamiento-de-bases-de-datos)
- [Manejo de errores y rate limiting](#manejo-de-errores-y-rate-limiting)
- [Configuración por ambiente](#configuración-por-ambiente)
- [Seguridad y pendientes conocidos](#seguridad-y-pendientes-conocidos)

---

## Visión general

Backend de la plataforma **Colmena**, cuyo propósito es permitir que los
estudiantes se registren y obtengan automáticamente bases de datos para sus
proyectos, en cualquiera de cuatro motores soportados.

### Stack tecnológico

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

### Arquitectura: database-centric

Toda la **lógica de negocio vive en Stored Procedures** dentro de SQL Server
(catálogo de usuarios y bases de datos). El backend en sí es un **mediador
HTTP**: expone endpoints REST, gestiona JWT/OAuth, valida entrada, invoca los
SPs correspondientes y traduce el resultado (o el error) a una respuesta HTTP
uniforme.

Consecuencia importante para el mantenimiento del proyecto: **los Stored
Procedures no están versionados en este repositorio** — viven directamente en
la base de datos remota. Cambios de esquema o de contrato de un SP deben
coordinarse manualmente contra el servidor real.

#### Capas del proyecto

| Carpeta | Responsabilidad |
|---|---|
| `Controllers/` | Puntos de entrada HTTP (`AuthController`, `DatabasesController`, `StatisticsController`). Sin lógica de negocio — delegan en servicios. |
| `Services/` | Orquestación: `AuthService`, `DatabaseProvisioningService`, `JwtTokenService`, `OAuthRedirectBuilder`, `PasswordGenerator`. |
| `Repository/` | Acceso a datos vía SPs (`FromSqlRaw` + `SqlParameter` tipados) — `UserRepository`, `DatabaseRepository`, `StatisticsRepository`. |
| `Provisioners/` | Un provisioner por motor de base de datos (patrón *Strategy*): `SqlServerProvisioner`, `PostgresProvisioner`, `MySqlProvisioner`, `MongoProvisioner`. |
| `Middleware/` | `ExceptionHandlingMiddleware` (traduce excepciones a JSON uniforme) y las excepciones propias (`AppException`, `AuthException`). |
| `DTOs/` | Contratos de entrada/salida de cada endpoint. |
| `Models/` | Entidades mapeadas desde los SPs (`ProvisionedDatabaseInfo`, `UserIdentity`, `LoginInfo`, etc.). |

#### Aprovisionamiento multi-motor (patrón Strategy)

`IDatabaseProvisionerFactory` recibe el motor solicitado (`SqlServer`,
`Postgres`, `MySql`, `Mongo`) y selecciona la implementación de
`IDatabaseProvisioner` correspondiente. Cada provisioner sabe crear un
usuario/rol y una base de datos físicos en su motor, aplicando permisos
acotados solo a esa base (nunca privilegios globales).

### Flujo de una petición típica

```
Cliente → Controller → Service (regla de orquestación)
                     → Repository → Stored Procedure (SQL Server remoto)
                     → Provisioner (si aplica: crea la BD física en el motor pedido)
        ← Middleware de excepciones traduce cualquier error a {status, error}
```

---

## Autenticación

El backend soporta dos mecanismos de inicio de sesión: **por contraseña**
(email + password propios) y **OAuth 2.0** contra Google y GitHub. Ambos
terminan en la emisión del mismo tipo de token: un **JWT firmado por el
backend**.

### JSON Web Token (JWT)

- Emitido por `JwtTokenService`, firmado con clave simétrica (`Jwt:Key` en
  configuración) y validado en cada request protegido vía
  `Microsoft.AspNetCore.Authentication.JwtBearer`.
- Duración configurable (`Jwt:ExpirationMinutes`, hoy 60 minutos).
- Claims incluidos: `sub` / `UserId`, `email`, `role` (`Admin`, `Student` o
  `Developer`). **No** incluye `fullName`.
- Se envía en cada request protegido como:
  ```
  Authorization: Bearer <token>
  ```
- Si el token falta, es inválido o expiró, el middleware de autenticación
  corta la petición **antes** de llegar al controller y responde `401` con
  cuerpo vacío (no usa el formato uniforme `{status, error}` del resto de la
  API).

### Login por contraseña

`POST /auth/register` y `POST /auth/login` (ver [Referencia de la API](#referencia-de-la-api)
para el detalle completo de bodies y errores). Las contraseñas se hashean con
**BCrypt** antes de persistirse; el hash nunca se serializa en ninguna
respuesta.

Al primer registro o primer login exitoso **por contraseña**, el backend
aprovisiona automáticamente una base de datos **MySQL** a nombre del usuario
(`AuthService.EnsureMySqlDatabaseAsync`). Es idempotente — si el usuario ya
tiene una BD MySQL, no crea otra — y **no bloquea el login si falla** (se
registra el error y el campo queda `null`; el usuario puede pedirla luego con
`POST /databases`).

### Login con Google / GitHub (OAuth 2.0)

El flujo completo lo maneja el backend; el frontend solo redirige el
navegador a un endpoint de inicio.

```
1. El frontend redirige el navegador a  GET /auth/{provider}/login
2. El backend redirige a la pantalla de consentimiento del proveedor
3. El usuario acepta
4. El proveedor llama de vuelta al callback interno del backend
   (Google: /signin-google · GitHub: callback propio de AspNet.Security.OAuth.GitHub)
5. El backend resuelve la identidad (vía la cookie temporal "External"),
   invoca sp_UpsertExternalLogin y firma el JWT
6. El backend redirige al frontend con los datos de sesión en la query string
```

Configuración en `Program.cs`:

- `AddGoogle("Google", ...)` — usa `Authentication:Google:ClientId` /
  `ClientSecret` desde configuración (nunca hardcodeado). `SignInScheme =
  "External"`.
- `AddGitHub("GitHub", ...)` — mismo patrón, además pide el scope
  `user:email` porque GitHub puede devolver el correo como privado (el
  handler llama a `/user/emails` cuando aplica).
- Cookie temporal `"External"` (`Colmena.External`), vida de 5 minutos —
  solo almacena la identidad externa mientras se resuelve el callback, no es
  la sesión final del usuario.

#### Redirect al frontend tras el callback

El backend redirige a:

```
{Frontend:BaseUrl}/oauth/callback?token=...&expiresAt=...&userId=...&email=...&fullName=...&role=...
```

o, si algo falla, a `{Frontend:BaseUrl}/oauth/callback?error=<mensaje>`.
`Frontend:BaseUrl` es configuración por ambiente (ver
[Configuración por ambiente](#configuración-por-ambiente)).

> ⚠️ **Nota de seguridad — pendiente.** El JWT y los datos del usuario
> (`email`, `fullName`, `role`, `userId`) viajan como **parámetros de query
> string**, no en el fragmento (`#`) ni por `POST`. Esto los expone al
> historial del navegador, a logs de acceso del servidor/proxy, y al header
> `Referer` si la página de callback carga recursos de terceros. Es un
> hallazgo de seguridad conocido y abierto — ver
> [Seguridad y pendientes conocidos](#seguridad-y-pendientes-conocidos).
> **Mientras no se corrija, el frontend debe leer los parámetros y llamar
> inmediatamente `history.replaceState(...)`** para no dejarlos en el
> historial del navegador.

#### OAuth no aprovisiona la BD MySQL automáticamente

A diferencia del login por contraseña, un primer login por Google/GitHub
**no** crea la BD MySQL automáticamente (se removió deliberadamente: como la
respuesta OAuth viaja por redirect y no como JSON, la contraseña generada se
perdía sin que el usuario la viera nunca). El frontend debe, tras resolver el
callback, llamar `GET /databases` y si viene vacío, pedir explícitamente
`POST /databases` con `{"engine": "MySql", "dbName": "principal"}`.

### Configurar el redirect URI de OAuth en Google Cloud Console

Para que `GET /auth/google/login` funcione en cualquier ambiente (local, QA,
producción), el **Authorized redirect URI** configurado en el proyecto de
Google Cloud Console debe coincidir **exactamente** (esquema, host, path, sin
slash de más o de menos) con el callback interno que genera
`Microsoft.AspNetCore.Authentication.Google` para ese host:

```
https://<host-del-ambiente>/signin-google
```

Puntos a verificar por ambiente:

- El dominio del ambiente (ej. `docs.idempotencia.andrescortes.dev` en QA)
  debe estar en **Authorized domains** de la pantalla de consentimiento OAuth
  del proyecto de Google Cloud.
- Si el proyecto de Google Cloud sigue en modo **Testing**, solo los correos
  agregados como **Test users** pueden autenticarse — cualquier otra cuenta
  recibe `Error 400: invalid_request` (no cumple la política de OAuth 2.0).
- Cada ambiente (Development / QA / Producción) puede necesitar su propio
  `ClientId`/`ClientSecret` si usan client IDs de OAuth separados, o bien
  agregar **todos** los redirect URIs de todos los ambientes al mismo client
  ID si se comparte uno solo.

---

## Referencia de la API

Todas las respuestas son JSON con campos en **camelCase** (ej. `fullName`,
`expiresAt`). El detalle exhaustivo de errores por endpoint está en
[Manejo de errores y rate limiting](#manejo-de-errores-y-rate-limiting).

### Datos base

| Dato | Valor |
|---|---|
| Formato | JSON (`Content-Type: application/json`) |
| Autenticación | `Authorization: Bearer <token>` en los endpoints protegidos |
| Total de endpoints de negocio | 9 (6 en `AuthController`, 2 en `DatabasesController`, 1 en `StatisticsController`) |

### Autenticación (endpoints)

#### `POST /auth/register`
Anónimo · rate limit `auth` (10/min/IP)

**Body:**
```json
{ "email": "ana@uni.edu", "password": "MiClaveSegura123", "fullName": "Ana Pérez" }
```

| Campo | Validación |
|---|---|
| `email` | Requerido, formato email, máx. 150 caracteres |
| `password` | Requerido, 8–100 caracteres |
| `fullName` | Requerido, máx. 150 caracteres |

**200 OK** → `AuthResponse` (ver abajo), con `mySqlDatabase` siempre poblado
(es el primer login del usuario).

#### `POST /auth/login`
Anónimo · rate limit `auth` (10/min/IP)

**Body:**
```json
{ "email": "ana@uni.edu", "password": "MiClaveSegura123" }
```

**200 OK** → `AuthResponse`. `mySqlDatabase` viene poblado solo la primera vez
que este usuario se autentica por contraseña; si no, `null`.

#### `GET /auth/google/login` · `GET /auth/github/login`
Anónimo. Redirige (`302`) a la pantalla de consentimiento del proveedor.

#### `GET /auth/google/callback` · `GET /auth/github/callback`
Anónimo (requiere la cookie temporal `External`). Redirige al frontend con
los datos de sesión en la query string, o con `?error=...` si falla.

#### `AuthResponse` (forma de respuesta común)

```json
{
  "token": "eyJhbGciOi...",
  "expiresAt": "2026-07-16T18:45:00Z",
  "userId": 12,
  "email": "ana@uni.edu",
  "fullName": "Ana Pérez",
  "role": "Student",
  "mySqlDatabase": null
}
```

| Campo | Tipo | Notas |
|---|---|---|
| `token` | string | JWT — usar en `Authorization: Bearer` |
| `expiresAt` | string (ISO 8601 UTC) | A partir de esta hora, cualquier request protegido devuelve `401` |
| `userId` | number | Id propio del usuario |
| `email`, `fullName` | string | Datos propios |
| `role` | string | `Admin`, `Student` o `Developer` |
| `mySqlDatabase` | objeto \| `null` | Credenciales de la BD MySQL auto-aprovisionada, solo la vez que se crea |

### Bases de datos (endpoints protegidos, requieren `Authorization: Bearer`)

#### `POST /databases`
Rate limit `db-provisioning` (5/min por usuario, además del límite global)

**Body:**
```json
{ "engine": "SqlServer", "dbName": "proyecto_ana", "maxConcurrentConnections": 10 }
```

| Campo | Requerido | Notas |
|---|---|---|
| `engine` | Sí | `"SqlServer"`, `"Postgres"`, `"MySql"` o `"Mongo"`. Máx. 20 caracteres. |
| `dbName` | Sí | Máx. 128 caracteres. El backend antepone un prefijo por usuario (ej. `colmena_u12_...`). |
| `maxConcurrentConnections` | No | Entero 1–100. Si se omite, usa el default del motor. Siempre se acota a un tope duro por motor sin importar lo pedido. Solo tiene efecto real en MySQL/Postgres. |

**201 Created:**
```json
{
  "databaseId": 5,
  "engine": "SqlServer",
  "dbName": "colmena_u12_proyecto_ana",
  "status": "Active",
  "maxStorageMB": 20,
  "maxConcurrentConnections": 10,
  "host": "100.99.206.50",
  "port": 1433,
  "loginName": "usr_colmena_u12_proyecto_ana",
  "password": "P4ssGeneradaUnaVez"
}
```

> ⚠️ `password` son las credenciales reales de acceso a la BD física y
> **solo se entregan en esta respuesta** — no se pueden recuperar después (el
> backend solo guarda el hash).

#### `GET /databases`
Sin rate limit dedicado (solo el global).

**200 OK:**
```json
[
  {
    "databaseId": 5,
    "engine": "SqlServer",
    "dbName": "colmena_u12_proyecto_ana",
    "status": "Active",
    "maxStorageMB": 20,
    "currentSizeMB": 3.5,
    "lastActivityAt": "2026-07-16T12:00:00Z",
    "createdAt": "2026-07-01T09:00:00Z",
    "pausedAt": null
  }
]
```

No incluye `loginName` ni `password` — esas credenciales solo se entregan una
vez, en el momento de la creación.

### Estadísticas de la plataforma (solo Admin)

#### `GET /statistics`
Requiere JWT con claim `role = "Admin"`.

**200 OK:**
```json
{
  "totalUsers": 120,
  "activeUsers": 98,
  "totalDatabases": 45,
  "activeDatabases": 40,
  "totalLogins": 530,
  "serviceAvailable": true
}
```

Son conteos agregados de toda la plataforma — no expone datos individuales de
ningún usuario ni de ninguna BD en particular.

### Documentación interactiva (solo `Development`)

Cuando `ASPNETCORE_ENVIRONMENT=Development`, el backend expone:

| Ruta | Qué es |
|---|---|
| `/openapi/v1.json` | Documento OpenAPI autogenerado |
| `/swagger` | Swagger UI |
| `/scalar` | Scalar API Reference |

Ninguna de las tres está disponible fuera de `Development`.

---

## Aprovisionamiento de bases de datos

Cada usuario puede tener una o más bases de datos físicas, aprovisionadas a
demanda vía `POST /databases` (o automáticamente en el primer login por
contraseña — ver [Autenticación](#autenticación)).

### Motores soportados

Los 4 motores tienen provisioner real implementado (creación de
usuario/rol + base de datos + permisos acotados a esa BD, nunca privilegios
globales):

| Motor | Puerto por defecto | Provisioner |
|---|---|---|
| SQL Server | 1433 | `SqlServerProvisioner` |
| PostgreSQL | 5432 | `PostgresProvisioner` |
| MySQL | 3306 | `MySqlProvisioner` |
| MongoDB | 27017 | `MongoProvisioner` |

### Cuota de almacenamiento (`MaxStorageMB`)

- **SQL Server**: aplicada nativamente con `MAXSIZE = {maxStorageMb}MB` en el
  `CREATE DATABASE` — el motor mismo rechaza escrituras que excedan el tope.
- **PostgreSQL / MySQL / MongoDB**: estos motores **no tienen un equivalente
  nativo** de tamaño máximo por base de datos. No se hace cumplir todavía en
  producción para estos tres motores.

### Límite de conexiones concurrentes (`maxConcurrentConnections`)

Configurable por request al crear una BD (`CreateDatabaseRequest.MaxConcurrentConnections`,
opcional, rango 1–100). El backend resuelve el valor final así:

1. Si el cliente no pide nada → usa el default configurado por motor
   (`Provisioning:{Engine}:MaxConcurrentConnections`, hoy `5`).
2. Si el cliente pide un valor → se acota **siempre** a
   `Provisioning:{Engine}:MaxConcurrentConnectionsCap` (hoy `20`). El cliente
   nunca puede desactivar el control pidiendo un número arbitrariamente alto.

| Motor | Mecanismo nativo | Estado |
|---|---|---|
| MySQL | `MAX_USER_CONNECTIONS` | ✅ Aplicado |
| PostgreSQL | `CONNECTION LIMIT` | ✅ Aplicado |
| SQL Server | No existe límite nativo por login (requeriría *Resource Governor* o un logon trigger) | 🔴 Sin implementar |
| MongoDB | No existe límite por usuario (solo `net.maxIncomingConnections` a nivel de servidor) | 🔴 Sin implementar |

El valor efectivamente aplicado vuelve en la respuesta
(`CreateDatabaseResponse.MaxConcurrentConnections`); vale `0` en SqlServer/Mongo
porque ahí no se aplica.

### Aislamiento entre bases de datos de distintos usuarios

Cada estudiante tiene su propia base, pero por defecto ni SQL Server ni
Postgres restringen qué bases puede *ver* o *a cuáles conectarse* un
login/rol nuevo — solo lo que puede hacer una vez adentro. Mitigaciones
aplicadas:

- **SQL Server**: `DENY VIEW ANY DATABASE` al login nuevo, justo después de
  crearlo — oculta los nombres de las demás bases del servidor.
- **PostgreSQL**: `REVOKE CONNECT ... FROM PUBLIC` + `GRANT CONNECT ... TO`
  el dueño, justo después de crear la BD — ningún otro rol puede conectarse.
- **MongoDB**: no requirió cambios — los roles ya vienen *scoped* a una sola
  base por diseño del provisioner.
- **MySQL**: limitación conocida e inherente del motor — cualquier usuario
  autenticado puede ver los *nombres* de todas las bases del servidor
  (`SHOW DATABASES`, `information_schema.schemata`), aunque no puede leer sus
  datos. No hay forma estándar de ocultarlo sin vistas personalizadas sobre
  `information_schema`.

### Ciclo de vida (TTL) — pausado/eliminación automática por inactividad

El catálogo ya tiene las columnas necesarias (`LastActivityAt`, `PausedAt`,
`DeletedAt` en `ProvisionedDatabaseInfo`), pero **todavía no existe ningún
job** que las actualice o que pause/elimine bases inactivas. Es el ítem de
mayor esfuerzo pendiente del checklist de seguridad.

---

## Manejo de errores y rate limiting

La API devuelve **dos formatos de error distintos** según el origen.

### 1. Errores de validación de modelo — `400`

Generados automáticamente por `[ApiController]` (DataAnnotations), **antes**
de entrar al controller. Formato estándar `ValidationProblemDetails`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Email": ["The Email field is not a valid e-mail address."],
    "Password": ["The field Password must be a string with a minimum length of 8."]
  }
}
```

### 2. Errores de negocio / autenticación / servidor

Producidos por el middleware central `ExceptionHandlingMiddleware`. Formato
uniforme:

```json
{ "status": 401, "error": "Credenciales inválidas." }
```

### 3. Errores del middleware JWT — `401`

Cuando el token falta, es inválido o expiró, el middleware de autenticación
corta la petición **antes** de llegar al controller. Devuelve `401` con
header `WWW-Authenticate` y **cuerpo vacío** (no usa el formato `{status,
error}`).

### Tabla de códigos HTTP

| Código | Cuándo ocurre |
|---|---|
| `200 OK` | Operación correcta |
| `201 Created` | BD aprovisionada (`POST /databases`) |
| `400 Bad Request` | Validación fallida o regla de negocio de un SP |
| `401 Unauthorized` | Token ausente/inválido/expirado, credenciales incorrectas, cuenta inactiva o solo-OAuth |
| `403 Forbidden` | Autenticado pero sin el rol requerido (ej. `/statistics` sin rol `Admin`) |
| `404 Not Found` | Ruta inexistente |
| `405 Method Not Allowed` | Método HTTP incorrecto sobre una ruta válida |
| `415 Unsupported Media Type` | Falta `Content-Type: application/json` en un POST con body |
| `429 Too Many Requests` | Rate limit superado |
| `500 Internal Server Error` | Error no controlado, SP inexistente, fallo de conexión a la DB |

Los `500` genéricos **nunca filtran detalles internos** al cliente (mensaje
siempre genérico); el detalle real solo queda en los logs del servidor.

### Rate limiting

Implementado con el `RateLimiter` nativo de .NET (`Program.cs`). Todas las
particiones se calculan **por IP**, excepto `db-provisioning` que se
particiona **por usuario** (claim `UserId` del JWT).

| Ámbito | Política | Límite | Aplica a |
|---|---|---|---|
| Autenticación | `auth` (fixed window) | 10 peticiones/min por IP | `POST /auth/register`, `POST /auth/login` |
| Aprovisionamiento de BD | `db-provisioning` (fixed window) | 5 peticiones/min por usuario | `POST /databases` |
| Global | `GlobalLimiter` (fixed window) | 100 peticiones/min por IP | Toda la API |

Respuesta al superar el límite (`429`):

```json
{ "status": 429, "error": "Demasiadas solicitudes. Inténtalo más tarde." }
```

Se incluye el header `Retry-After` (segundos a esperar). No hay cola
(`QueueLimit = 0`): al superar el cupo, se rechaza de inmediato.

> ⚠️ **Detrás de un reverse proxy:** las particiones usan `RemoteIpAddress`.
> Detrás de un proxy inverso, todas las peticiones comparten la IP del proxy
> y el límite colapsa para todos los usuarios por igual. Hay que habilitar
> `ForwardedHeaders` (`X-Forwarded-For`) en el ambiente de despliegue para
> que llegue la IP real del cliente.

Los 4 endpoints OAuth (`/auth/google/*`, `/auth/github/*`) hoy **solo**
quedan cubiertos por el límite global (100/min/IP) — no tienen una política
`auth` dedicada como `/auth/login` y `/auth/register`.

---

## Configuración por ambiente

La configuración sigue el esquema estándar de ASP.NET Core:
`appsettings.json` (base, todos los ambientes) +
`appsettings.{Environment}.json` (overrides), más variables de entorno.
`ASPNETCORE_ENVIRONMENT` determina cuál se carga (`Development`, y los que se
definan para `QA`/`Production`).

> 🔒 **Secretos — no versionar en texto plano.** `appsettings.json` hoy
> contiene, en texto plano, la contraseña del usuario `sa` de SQL Server, la
> clave de firma JWT, los `ClientSecret` de Google y GitHub OAuth, y las
> credenciales de administrador de MySQL/MongoDB. Es un hallazgo de
> seguridad **abierto**. La recomendación es mover todo esto a **User
> Secrets** en desarrollo y a variables de entorno / un vault en QA y
> producción, dejando en `appsettings.json` solo la estructura con
> placeholders. Los valores reales no se incluyen aquí por ese mismo motivo.

### Secciones de configuración

| Sección | Qué controla |
|---|---|
| `Frontend:BaseUrl` | A dónde redirige el backend tras un login OAuth exitoso (`{BaseUrl}/oauth/callback`) |
| `Cors:AllowedOrigins` | Lista de orígenes permitidos por CORS. El backend **lanza un error al arrancar** si esta lista está vacía, en vez de fallar en silencio |
| `ConnectionStrings:Colmena` | Cadena de conexión a la base de datos de catálogo (SQL Server) |
| `Jwt:Issuer` / `Jwt:Audience` / `Jwt:Key` / `Jwt:ExpirationMinutes` | Configuración de emisión y validación del JWT propio |
| `Authentication:Google:ClientId` / `ClientSecret` | Credenciales de la app OAuth de Google |
| `Authentication:GitHub:ClientId` / `ClientSecret` | Credenciales de la app OAuth de GitHub |
| `Provisioning:{Engine}:Host` / `Port` / `AdminConnectionString` | Datos de conexión con privilegios de administrador para cada motor aprovisionable |
| `Provisioning:{Engine}:MaxConcurrentConnections` / `MaxConcurrentConnectionsCap` | Default y tope duro de conexiones concurrentes por BD aprovisionada (MySQL/Postgres) |

### Ambientes

**Development (local):**
- `ASPNETCORE_ENVIRONMENT=Development` (definido en `Properties/launchSettings.json`).
- URLs locales: `https://localhost:7113` (HTTPS) y `http://localhost:5175` (HTTP).
- Expone `/openapi/v1.json`, `/swagger` y `/scalar` (deshabilitados fuera de
  `Development`).
- El certificado HTTPS es autofirmado; si el cliente lo rechaza, se puede
  usar el puerto HTTP solo para pruebas (login/registro por contraseña
  funcionan en ambos, pero **OAuth necesita HTTPS**).

**QA / Producción:**
No hay un `appsettings.QA.json` ni `appsettings.Production.json` en el
repositorio todavía — para desplegar en un ambiente propio, la práctica
recomendada es:

1. Crear `appsettings.{Ambiente}.json` con los overrides específicos (o usar
   variables de entorno equivalentes), **sin** commitear secretos reales.
2. Ajustar `Cors:AllowedOrigins` y `Frontend:BaseUrl` al dominio real del
   frontend de ese ambiente.
3. Configurar el redirect URI de OAuth para el dominio de ese ambiente en
   Google Cloud Console / GitHub OAuth Apps (ver más abajo).
4. Si el despliegue va detrás de un reverse proxy, habilitar
   `ForwardedHeaders` (`X-Forwarded-For`) — de lo contrario el rate limiting
   (particionado por IP) colapsa porque todas las peticiones comparten la IP
   del proxy.

### Redirect URIs de OAuth por ambiente

Cada ambiente donde corra el backend necesita su propio **Authorized redirect
URI** dado de alta en el proveedor OAuth, con el dominio exacto de ese
ambiente:

```
https://<host-del-ambiente>/signin-google   (Google)
```

Checklist al agregar un ambiente nuevo (ej. QA):

- [ ] El dominio exacto (sin slash final de más/menos) está en **Authorized
      redirect URIs** del client ID de Google Cloud Console.
- [ ] El dominio (o su dominio raíz) está en **Authorized domains** de la
      pantalla de consentimiento OAuth.
- [ ] Si el proyecto de Google Cloud sigue en modo **Testing**, las cuentas
      que probarán el login están agregadas como **Test users** — de lo
      contrario Google devuelve `Error 400: invalid_request` para cualquier
      cuenta fuera de esa lista.
- [ ] `Cors:AllowedOrigins` incluye el dominio del frontend de ese ambiente.
- [ ] `Frontend:BaseUrl` apunta al frontend correcto para que el redirect
      post-login llegue al lugar esperado.

---

## Seguridad y pendientes conocidos

**Convención de estado:** 🔴 Abierto · 🟡 Fix entregado sin confirmar · 🟢
Resuelto (confirmado) · 🔵 Resuelto parcialmente.

### Alta severidad

| Hallazgo | Estado | Resumen |
|---|---|---|
| Token JWT en la query string del redirect OAuth | 🔴 Abierto | El JWT viaja como parámetro de URL en el callback OAuth, exponible en historial del navegador y logs. |
| Redirect OAuth también expone `email`/`fullName`/`role`/`userId` | 🔴 Abierto | Amplía el hallazgo anterior — mismo fix pendiente (código de un solo uso canjeable por `POST`). |
| Ciclo de vida (TTL) sin implementar | 🔴 Abierto | No hay pausado/eliminación automática de BDs inactivas todavía. |
| Secretos reales en texto plano en `appsettings.json` | 🔴 Abierto | Pendiente mover a User Secrets / vault. |
| Aislamiento entre usuarios en MySQL | 🔵 Parcial | SQL Server/Postgres/Mongo ya corregidos; MySQL tiene una limitación inherente del motor (nombres de otras BDs visibles, no sus datos). |

### Severidad media

| Hallazgo | Estado | Resumen |
|---|---|---|
| Cuota de almacenamiento no aplicada en Postgres/MySQL/Mongo | 🔴 Abierto | Solo SQL Server hace cumplir `MaxStorageMB` de forma nativa. |
| Límite de conexiones concurrentes sin equivalente nativo en SQL Server/Mongo | 🔵 Parcial | MySQL/Postgres ya resueltos y configurables por request. |
| Dependencia `Microsoft.OpenApi` con vulnerabilidad conocida | 🔴 Abierto | Alta severidad reportada por `dotnet build` (advisory `GHSA-v5pm-xwqc-g5wc`). |
| Sin rate limit dedicado en endpoints OAuth | 🔴 Abierto | Solo cubiertos por el límite global (100/min/IP), 10× más permisivo que `auth`. |

### Baja severidad / cosmético

| Hallazgo | Estado | Resumen |
|---|---|---|
| `CurrentSizeMB` sin tipo de columna explícito en EF Core | 🔴 Abierto | Riesgo de truncamiento silencioso de decimales. |
| Claim `"UserId"` duplicado como string literal | 🔴 Abierto | Sin constante compartida entre emisor y consumidor del claim. |
| `idempotencia.http` referencia un endpoint inexistente | 🔴 Abierto | Archivo de prueba obsoleto (`/weatherforecast/` de la plantilla por defecto). |

### Ya resuelto (para contexto)

- **Enumeración de cuentas OAuth-only en `POST /auth/login`** 🟢 — el login
  ya responde siempre el mismo mensaje genérico (`"Credenciales
  inválidas."`) sin importar si el correo no existe, el password es
  incorrecto, o la cuenta es solo-OAuth.
- **BD MySQL huérfana en login OAuth** 🟢 — se quitó el
  auto-aprovisionamiento de `ExternalLoginAsync`; el frontend debe pedirla
  explícitamente.
- **`sp_GetLoginByEmail` referenciaba una tabla `Roles` inexistente** 🟢 —
  confirmado corregido en vivo contra la base de datos real.

### Nota sobre la base de datos de catálogo

Los Stored Procedures que sostienen la lógica de negocio **no están
versionados en este repositorio** (arquitectura *database-centric*). Cualquier
cambio de esquema debe verificarse manualmente contra el servidor real antes
de asumir que un endpoint funciona de punta a punta en un ambiente nuevo.
