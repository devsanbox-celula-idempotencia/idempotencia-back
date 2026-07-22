---
id: api-referencia
title: Referencia de la API
sidebar_position: 3
sidebar_label: Referencia de la API
---

# Referencia de la API

Todas las respuestas son JSON con campos en **camelCase** (ej. `fullName`,
`expiresAt`). El detalle exhaustivo de errores por endpoint está en
[Manejo de errores](./manejo-de-errores).

## Datos base

| Dato | Valor |
|---|---|
| Formato | JSON (`Content-Type: application/json`) |
| Autenticación | `Authorization: Bearer <token>` en los endpoints protegidos |
| Total de endpoints de negocio | 9 (6 en `AuthController`, 2 en `DatabasesController`, 1 en `StatisticsController`) |

---

## Autenticación

### `POST /auth/register`
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

### `POST /auth/login`
Anónimo · rate limit `auth` (10/min/IP)

**Body:**
```json
{ "email": "ana@uni.edu", "password": "MiClaveSegura123" }
```

**200 OK** → `AuthResponse`. `mySqlDatabase` viene poblado solo la primera vez
que este usuario se autentica por contraseña; si no, `null`.

### `GET /auth/google/login` · `GET /auth/github/login`
Anónimo. Redirige (`302`) a la pantalla de consentimiento del proveedor. Ver
[Autenticación](./autenticacion) para el flujo completo.

### `GET /auth/google/callback` · `GET /auth/github/callback`
Anónimo (requiere la cookie temporal `External`). Redirige al frontend con
los datos de sesión en la query string, o con `?error=...` si falla.

### `AuthResponse` (forma de respuesta común)

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

---

## Bases de datos *(requieren `Authorization: Bearer`)*

### `POST /databases`
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

:::caution
`password` son las credenciales reales de acceso a la BD física y **solo se
entregan en esta respuesta** — no se pueden recuperar después (el backend
solo guarda el hash).
:::

### `GET /databases`
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

---

## Estadísticas de la plataforma *(solo Admin)*

### `GET /statistics`
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

---

## Documentación interactiva (solo `Development`)

Cuando `ASPNETCORE_ENVIRONMENT=Development`, el backend expone:

| Ruta | Qué es |
|---|---|
| `/openapi/v1.json` | Documento OpenAPI autogenerado |
| `/swagger` | Swagger UI |
| `/scalar` | Scalar API Reference |

Ninguna de las tres está disponible fuera de `Development`.
