---
id: manejo-de-errores
title: Manejo de Errores y Rate Limiting
sidebar_position: 5
sidebar_label: Errores y Rate Limiting
---

# Manejo de errores y rate limiting

La API devuelve **dos formatos de error distintos** según el origen.

## 1. Errores de validación de modelo — `400`

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

## 2. Errores de negocio / autenticación / servidor

Producidos por el middleware central `ExceptionHandlingMiddleware`. Formato
uniforme:

```json
{ "status": 401, "error": "Credenciales inválidas." }
```

## 3. Errores del middleware JWT — `401`

Cuando el token falta, es inválido o expiró, el middleware de autenticación
corta la petición **antes** de llegar al controller. Devuelve `401` con
header `WWW-Authenticate` y **cuerpo vacío** (no usa el formato `{status,
error}`).

## Tabla de códigos HTTP

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

## Excepciones internas y su traducción HTTP

| Excepción (C#) | Origen | Traducción HTTP |
|---|---|---|
| `ValidationProblem` (automática) | `[ApiController]` valida el DTO | `400` + `errors{}` |
| `AuthException` | Credenciales/estado de cuenta/token inválido | `401` + `{status,error}` |
| `AppException` (base) | Regla controlada del backend | `400` (por defecto) + `{status,error}` |
| `SqlException` (`Number >= 50000`) | `THROW`/`RAISERROR` de un SP (regla de negocio) | `400` + mensaje del SP |
| `SqlException` (`Number < 50000`) | SP inexistente, error de conexión, timeout, sintaxis | `500` (genérico) |
| `InvalidOperationException` | Un SP no devolvió las filas esperadas | `500` (genérico) |
| Cualquier otra `Exception` | No controlada | `500` (genérico) |

## Rate limiting

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

:::caution Detrás de un reverse proxy
Las particiones usan `RemoteIpAddress`. Detrás de un proxy inverso, todas las
peticiones comparten la IP del proxy y el límite colapsa para todos los
usuarios por igual. Hay que habilitar `ForwardedHeaders` (`X-Forwarded-For`)
en el ambiente de despliegue para que llegue la IP real del cliente.
:::

Los 4 endpoints OAuth (`/auth/google/*`, `/auth/github/*`) hoy **solo**
quedan cubiertos por el límite global (100/min/IP) — no tienen una política
`auth` dedicada como `/auth/login` y `/auth/register`.

## Patrón recomendado para el frontend

```js
async function apiCall(url, options = {}) {
  const res = await fetch(url, options);

  if (res.status === 429) {
    const retryAfter = res.headers.get("Retry-After");
    throw new Error(`Demasiadas solicitudes, reintenta en ${retryAfter}s`);
  }

  if (!res.ok) {
    const body = await res.json().catch(() => null);
    const message = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : (body?.error ?? "Error inesperado");
    throw new Error(message);
  }

  return res.status === 201 || res.status === 200 ? res.json() : null;
}
```
