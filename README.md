# idempotencia-back — API Colmena

Backend **.NET 10 + EF Core** (arquitectura *database-centric*) que aprovisiona
bases de datos SQL Server para estudiantes. Toda la lógica de negocio vive en
**Stored Procedures**; el backend es un mediador que expone HTTP, gestiona
JWT/OAuth e invoca los SPs.

> Guía de consumo para el frontend: [`docs/API.md`](docs/API.md).

---

## Índice

- [Modelo de errores](#modelo-de-errores)
- [Tabla de códigos HTTP](#tabla-de-códigos-http)
- [Excepciones internas y cómo se traducen](#excepciones-internas-y-cómo-se-traducen)
- [Errores posibles por endpoint](#errores-posibles-por-endpoint)
- [Errores de transporte comunes](#errores-de-transporte-comunes)
- [Rate limiting](#rate-limiting)

---

## Modelo de errores

La API devuelve **dos formatos de error distintos** según el origen:

### 1. Errores de validación de modelo — `400`
Los genera automáticamente `[ApiController]` **antes** de entrar al controller
(DataAnnotations). Formato estándar `ValidationProblemDetails`:

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
Los produce el middleware central [`ExceptionHandlingMiddleware`](Middleware/ExceptionHandlingMiddleware.cs).
Formato uniforme:

```json
{
  "status": 401,
  "error": "Credenciales inválidas."
}
```

### 3. Errores del propio middleware JWT — `401`
Cuando el token falta, es inválido o expiró, el middleware de autenticación
corta la petición **antes** de llegar al controller. Devuelve `401` con el
header `WWW-Authenticate` y **cuerpo vacío** (no usa el formato `{status,error}`).

---

## Tabla de códigos HTTP

| Código | Cuándo ocurre |
|--------|---------------|
| `200 OK` | Operación correcta |
| `201 Created` | BD aprovisionada (`POST /databases`) |
| `400 Bad Request` | Validación de modelo fallida **o** regla de negocio de un SP (`THROW >= 50000`) |
| `401 Unauthorized` | Token ausente/inválido/expirado, credenciales incorrectas, cuenta inactiva o solo-OAuth |
| `404 Not Found` | Ruta inexistente |
| `405 Method Not Allowed` | Método HTTP incorrecto sobre una ruta válida |
| `415 Unsupported Media Type` | Falta `Content-Type: application/json` en un POST con body |
| `500 Internal Server Error` | Error no controlado, SP inexistente, fallo de conexión a la DB |

---

## Excepciones internas y cómo se traducen

| Excepción (C#) | Origen | Traducción HTTP |
|----------------|--------|-----------------|
| `ValidationProblem` (automática) | `[ApiController]` valida el DTO | `400` + `errors{}` |
| [`AuthException`](Middleware/AppExceptions.cs) | Credenciales/estado de cuenta/token inválido | `401` + `{status,error}` |
| [`AppException`](Middleware/AppExceptions.cs) (base) | Regla controlada del backend | `400` (por defecto) + `{status,error}` |
| `SqlException` con `Number >= 50000` | `THROW`/`RAISERROR` de un SP (regla de negocio) | `400` + mensaje del SP |
| `SqlException` con `Number < 50000` | SP inexistente, error de conexión, timeout, sintaxis | `500` (mensaje genérico) |
| `InvalidOperationException` | `result.First()` cuando un SP **no devuelve filas** | `500` (genérico) |
| `BcryptAuthenticationException` / `SaltParseException` | Hash almacenado corrupto o no-BCrypt en `Verify` | `500` (genérico) |
| `Exception` (cualquier otra) | No controlada | `500` (genérico) |

> Los `500` genéricos **no filtran detalles internos** al cliente; el detalle
> real se registra con `ILogger` en el servidor.

---

## Errores posibles por endpoint

### `POST /auth/register`
| Código | Causa | Detalle |
|--------|-------|---------|
| `400` | Validación | `email` inválido/>150, `password` <8 o >100, `fullName` vacío/>150 |
| `400` | Negocio (SP) | `sp_RegisterUser` lanza `THROW 50001` → *"El correo ya está registrado."* |
| `500` | `InvalidOperationException` | El SP no devolvió la fila de identidad esperada |
| `500` | `SqlException` | El SP no existe, o falla la conexión a la DB |

### `POST /auth/login`
| Código | Causa | Detalle |
|--------|-------|---------|
| `400` | Validación | `email` inválido, `password` vacío |
| `401` | `AuthException` | *"Credenciales inválidas."* (correo no existe o contraseña incorrecta) |
| `401` | `AuthException` | *"Esta cuenta usa inicio de sesión externo."* (usuario sin `PasswordHash`, solo OAuth) |
| `401` | `AuthException` | *"La cuenta está inactiva."* (`IsActive = 0`) |
| `500` | `SaltParseException` | El `PasswordHash` en la DB no es un hash BCrypt válido |
| `500` | `SqlException` | Fallo de conexión / SP inexistente |

### `GET /auth/google/login` · `GET /auth/github/login`
| Código | Causa | Detalle |
|--------|-------|---------|
| `302` | Normal | Redirección al proveedor OAuth |
| — | Config | Si `ClientId`/`ClientSecret` faltan o el *redirect URI* no coincide, el **proveedor** muestra su propia página de error (no la API) |

### `GET /auth/google/callback` · `GET /auth/github/callback`
| Código | Causa | Detalle |
|--------|-------|---------|
| `401` | `AuthException` | *"No se pudo completar la autenticación externa."* (el usuario canceló o la cookie temporal expiró) |
| `401` | `AuthException` | *"El proveedor externo no entregó los datos mínimos (id/email)."* (típico en GitHub con email privado) |
| `500` | `InvalidOperationException` | `sp_UpsertExternalLogin` no devolvió identidad |
| `500` | `SqlException` | Fallo de conexión / SP inexistente |

### `POST /databases`  *(requiere `Authorization: Bearer`)*
| Código | Causa | Detalle |
|--------|-------|---------|
| `401` | JWT middleware | Token ausente, inválido o expirado (cuerpo vacío) |
| `401` | `AuthException` | *"El token no contiene un identificador de usuario válido."* (claim `UserId` ausente/corrupto) |
| `400` | Validación | `dbName` vacío o >128 |
| `400` | Negocio (SP) | `sp_CreateDatabase` lanza `THROW >= 50000` (cuota excedida, límite de BDs por usuario, nombre duplicado) |
| `500` | `SqlException` | ⚠️ **`sp_CreateDatabase` aún no existe en la DB** → hoy devuelve `500` |
| `500` | `InvalidOperationException` | El SP no devolvió la fila con credenciales |

### `GET /databases`  *(requiere `Authorization: Bearer`)*
| Código | Causa | Detalle |
|--------|-------|---------|
| `401` | JWT middleware | Token ausente, inválido o expirado |
| `401` | `AuthException` | Claim `UserId` inválido en el token |
| `500` | `SqlException` | ⚠️ **`sp_GetUserDatabases` aún no existe en la DB** → hoy devuelve `500` |

---

## Errores de transporte comunes

Aplican a **cualquier** endpoint:

| Código | Causa |
|--------|-------|
| `404` | La ruta no existe (typo en la URL) |
| `405` | Método incorrecto (ej. `GET` sobre `/auth/login`) |
| `415` | POST con body sin header `Content-Type: application/json` |
| `499`/timeout | El cliente cancela; el backend propaga el `CancellationToken` a la DB |
| `500` | Cualquier excepción no controlada (se registra en logs) |

---

## Rate limiting

✅ **Implementado** con el `RateLimiter` nativo de .NET (configurado en
[`Program.cs`](Program.cs)). Todas las particiones se calculan **por IP**.

| Ámbito | Política | Límite | Aplica a |
|--------|----------|--------|----------|
| Autenticación | `auth` (*fixed window*) | **10 peticiones / minuto** por IP | `POST /auth/register`, `POST /auth/login` |
| Global | `GlobalLimiter` (*fixed window*) | **100 peticiones / minuto** por IP | Toda la API |

### Respuesta al superar el límite — `429`

```json
{
  "status": 429,
  "error": "Demasiadas solicitudes. Inténtalo más tarde."
}
```

- Se incluye el header **`Retry-After`** (segundos a esperar) cuando el límite lo expone.
- Sin cola (`QueueLimit = 0`): al superar el cupo se rechaza de inmediato.

### Tabla resumida

| Código | Causa |
|--------|-------|
| `429 Too Many Requests` | Se superó el límite de la ventana (por IP). El header `Retry-After` indica los segundos a esperar. |

> ✅ **Detrás de proxy inverso (despliegue):** resuelto — `Program.cs` habilita
> `ForwardedHeaders` (`X-Forwarded-For` + `X-Forwarded-Proto`), así que la
> partición usa la IP real del cliente y no la del proxy. Este mismo cambio
> corrigió de paso un `redirect_uri_mismatch` en el login OAuth de Google
> (ver `docs/bugs.md` ítem 17).
