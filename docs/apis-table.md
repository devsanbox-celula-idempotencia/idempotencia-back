# Tabla de endpoints — API Colmena

Referencia rápida de todos los endpoints de negocio: método, ruta, auth,
rate limit, body de entrada y respuesta. Para el detalle de errores por
endpoint ver [`docs/API.md`](API.md); para el estado funcional de cada ruta
ver [`docs/routes.md`](routes.md).

## Índice

- [Auth](#auth)
- [Databases](#databases)
- [Statistics](#statistics)

---

## Auth

### `POST /auth/register`

| | |
|---|---|
| Auth | Anónimo |
| Rate limit | `auth` (10/min/IP) |

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "Segura123",
  "fullName": "Ana Pérez"
}
```

| Campo | Tipo | Validación |
|---|---|---|
| `email` | string | Requerido, formato email, máx. 150 caracteres |
| `password` | string | Requerido, 8–12 caracteres |
| `fullName` | string | Requerido, solo letras/espacios/apóstrofes/guiones/puntos, máx. 150 caracteres |

**200 OK** → `AuthResponse` (ver abajo), con `mySqlDatabase` poblado si es el primer login por contraseña del usuario.

---

### `POST /auth/login`

| | |
|---|---|
| Auth | Anónimo |
| Rate limit | `auth` (10/min/IP) |

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "Segura123"
}
```

| Campo | Tipo | Validación |
|---|---|---|
| `email` | string | Requerido, formato email |
| `password` | string | Requerido |

**200 OK** → `AuthResponse`. `mySqlDatabase` solo viene poblado la primera vez que el usuario se autentica por contraseña; si no, `null`.

---

### `GET /auth/google/login` · `GET /auth/github/login`

| | |
|---|---|
| Auth | Anónimo |
| Body | — |

**302** → redirige a la pantalla de consentimiento del proveedor (Google/GitHub).

---

### `GET /auth/google/callback` · `GET /auth/github/callback`

| | |
|---|---|
| Auth | Anónimo (requiere cookie temporal `External`) |
| Body | — |

**302** → redirige al frontend: `{Frontend:BaseUrl}/oauth/callback?token=...&expiresAt=...&userId=...&email=...&fullName=...&role=...`, o `?error=<mensaje>` si falla.

---

### `AuthResponse` (forma común de respuesta)

```json
{
  "token": "eyJhbGciOi...",
  "expiresAt": "2026-07-16T18:45:00Z",
  "userId": 12,
  "email": "ana@uni.edu",
  "fullName": "Ana Pérez",
  "role": "Student",
  "mySqlDatabase": {
    "databaseId": 5,
    "engine": "MySql",
    "dbName": "colmena_u12_principal",
    "host": "100.99.206.50",
    "port": 3306,
    "loginName": "usr_colmena_u12_principal",
    "password": "P4ssGeneradaUnaVez"
  }
}
```

`mySqlDatabase` es `null` salvo la primera vez que se aprovisiona (la contraseña ahí solo se entrega esa vez).

---

## Databases

*Todos requieren `Authorization: Bearer <token>`.*

### `POST /databases`

| | |
|---|---|
| Auth | JWT |
| Rate limit | `db-provisioning` (5/min/usuario) + límite global |

**Body:**
```json
{
  "engine": "SqlServer",
  "dbName": "proyecto_ana",
  "maxConcurrentConnections": 10
}
```

| Campo | Requerido | Validación |
|---|---|---|
| `engine` | Sí | `"SqlServer"`, `"Postgres"`, `"MySql"` o `"Mongo"`. Máx. 20 caracteres |
| `dbName` | Sí | Letras/dígitos/guion bajo, empieza con letra, 3–128 caracteres |
| `maxConcurrentConnections` | No | Entero 1–100. Se acota igual a un tope duro del motor. Solo tiene efecto real en MySQL/Postgres |

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

`password` solo se entrega en esta respuesta — no se puede recuperar después.

---

### `GET /databases`

| | |
|---|---|
| Auth | JWT |
| Rate limit | Solo el global |
| Body | — |

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

No incluye `loginName` ni `password`.

---

### `GET /databases/{id}`

| | |
|---|---|
| Auth | JWT |
| Rate limit | Solo el global |
| Body | — |

Detalle de una BD puntual — pensado para cuando el usuario perdió sus datos de conexión (host/puerto/usuario).

**200 OK:**
```json
{
  "databaseId": 5,
  "engine": "SqlServer",
  "dbName": "colmena_u12_proyecto_ana",
  "status": "Active",
  "host": "100.99.206.50",
  "port": 1433,
  "loginName": "usr_colmena_u12_proyecto_ana",
  "maxStorageMB": 20,
  "currentSizeMB": 3.5,
  "lastActivityAt": "2026-07-16T12:00:00Z",
  "createdAt": "2026-07-01T09:00:00Z",
  "pausedAt": null,
  "deletedAt": null
}
```

Nunca incluye `password` (no se puede recuperar; usar `reset-password`).

---

### `POST /databases/{id}/deactivate`

| | |
|---|---|
| Auth | JWT |
| Rate limit | `db-provisioning` (5/min/usuario) |
| Body | — |

Revoca el acceso físico a la BD (login/usuario deshabilitado en el motor) sin borrar los datos. Requiere que la BD esté `Active`; si no, `400`. Paso obligatorio antes de poder eliminarla.

**200 OK** → `DatabaseDetailResponse` (mismo shape que `GET /databases/{id}`) con `status: "Inactive"`.

---

### `DELETE /databases/{id}`

| | |
|---|---|
| Auth | JWT |
| Rate limit | `db-provisioning` (5/min/usuario) |
| Body | — |

Elimina definitivamente la BD (borrado físico real). Requiere que la BD ya esté `Inactive` (haber pasado por `deactivate`); si no, `400`.

**204 No Content**

---

### `POST /databases/{id}/reset-password`

| | |
|---|---|
| Auth | JWT |
| Rate limit | `db-provisioning` (5/min/usuario) |
| Body | — |

Genera una contraseña nueva, la aplica en el motor físico y la envía por correo al usuario autenticado. Requiere que la BD esté `Active`; si no, `400`.

**200 OK:**
```json
{
  "status": 200,
  "message": "Se envió la nueva contraseña a tu correo."
}
```

La contraseña **nunca** viaja en la respuesta HTTP — solo por correo.

---

## Statistics

### `GET /statistics`

| | |
|---|---|
| Auth | JWT con claim `role = "Admin"` |
| Body | — |

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

Conteos agregados de toda la plataforma — no expone datos individuales de ningún usuario ni BD.
