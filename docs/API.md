# API Colmena — Guía de consumo (Frontend)

Documentación para integrar el frontend con el backend de autenticación y
bases de datos de Colmena.

---

## 1. Datos base

| Dato | Valor (desarrollo) |
|------|--------------------|
| Base URL | `https://localhost:7113` |
| Formato | JSON (`Content-Type: application/json`) |
| Nombres de campo | **camelCase** (ej: `fullName`, `expiresAt`) |
| Autenticación | JWT Bearer en header `Authorization` |

> En desarrollo el certificado es autofirmado. Si tu cliente HTTP rechaza el
> certificado, usa el puerto HTTP `http://localhost:5175` **solo para pruebas**
> (OAuth necesita HTTPS, pero login/registro por contraseña funcionan en ambos).

---

## 2. Cómo funciona el JWT

Todos los endpoints de login/registro devuelven un **token JWT**. El frontend debe:

1. Guardar el `token` (ej. en memoria o `localStorage`).
2. Enviarlo en cada petición protegida en el header:
   ```
   Authorization: Bearer <token>
   ```
3. Renovar el login cuando el token expire (ver `expiresAt`).

El token contiene los claims `userId`, `email` y `role` (`Admin`, `Student` o
`Developer`), útiles para mostrar/ocultar vistas en el front.

---

## 3. Respuesta de autenticación (`AuthResponse`)

Todos los flujos de login/registro devuelven **la misma estructura**:

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2026-07-16T18:45:00Z",
  "userId": 12,
  "email": "ana@uni.edu",
  "fullName": "Ana Pérez",
  "role": "Student"
}
```

---

## 4. Endpoints de autenticación

### 4.1 Registro por contraseña

```
POST /auth/register
```

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "MiClaveSegura123",
  "fullName": "Ana Pérez"
}
```

**Reglas de validación:**
- `email`: requerido, formato email válido, máx. 150 caracteres.
- `password`: requerido, mín. 8 y máx. 100 caracteres.
- `fullName`: requerido, máx. 150 caracteres.

**Respuesta `200 OK`:** un `AuthResponse` (sección 3).

**Errores comunes:**
- `400` → validación fallida (ver sección 7.1) o correo ya registrado.

---

### 4.2 Login por contraseña

```
POST /auth/login
```

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "MiClaveSegura123"
}
```

**Respuesta `200 OK`:** un `AuthResponse` (sección 3).

**Errores comunes:**
- `401` → credenciales inválidas, cuenta inactiva, o la cuenta se creó solo con
  login externo (OAuth) y no tiene contraseña.

---

### 4.3 Login con Google / GitHub (OAuth)

El flujo OAuth lo maneja **el backend**. El frontend solo necesita **redirigir el
navegador** a los endpoints de inicio:

| Proveedor | URL para iniciar el login |
|-----------|---------------------------|
| Google | `https://localhost:7113/auth/google/login` |
| GitHub | `https://localhost:7113/auth/github/login` |

**Flujo completo:**

```
1. Front redirige el navegador a  /auth/google/login
2. Backend redirige a Google (pantalla de consentimiento)
3. Usuario acepta
4. Google → backend (/signin-google, interno)
5. Backend resuelve el usuario y firma el JWT
6. Backend redirige al frontend con los datos del AuthResponse en la query
```

Ejemplo desde el front (botón):
```js
// Simplemente navega el navegador; NO uses fetch para iniciar OAuth.
window.location.href = "https://localhost:7113/auth/google/login";
```

**El paso 6 ya redirige al frontend** (implementado en `OAuthRedirectBuilder`):

```
{Frontend:BaseUrl}/oauth/callback?token=...&expiresAt=...&userId=...&email=...&fullName=...&role=...
```

En caso de error, redirige con `?error=<mensaje>` en su lugar. `Frontend:BaseUrl`
se configura en `appsettings.json` (hoy `http://localhost:5555` en desarrollo).
El front debe implementar la ruta `/oauth/callback` para leer esos query params.

> ⚠️ **Nota de seguridad:** el token viaja como parámetro de **query string**
> (no en el fragmento `#` ni por POST), lo que lo expone a logs de acceso e
> historial del navegador. Es un hallazgo abierto — ver `docs/bugs.md` (ítem 1)
> para el detalle y la solución propuesta. Mientras no se corrija, el frontend
> debe limpiar la URL (`history.replaceState`) apenas lea el token.

---

## 5. Endpoints de bases de datos (protegidos)

Requieren header `Authorization: Bearer <token>`.

### 5.1 Crear (aprovisionar) una base de datos

```
POST /databases
```

**Body:**
```json
{
  "engine": "SqlServer",
  "dbName": "proyecto_ana"
}
```

- `engine`: **requerido**. Valores: `"SqlServer"`, `"Postgres"`, `"MySql"`, `"Mongo"`.
  Hoy solo `"SqlServer"` está implementado; los demás devuelven `501`.
- `dbName`: requerido, máx. 128 (el backend le antepone un prefijo por usuario).

**Respuesta `201 Created`:**
```json
{
  "databaseId": 5,
  "engine": "SqlServer",
  "dbName": "colmena_u12_proyecto_ana",
  "status": "Active",
  "maxStorageMB": 20,
  "host": "46.224.101.88",
  "port": 1433,
  "loginName": "usr_colmena_u12_proyecto_ana",
  "password": "P4ssGeneradaUnaVez"
}
```

> ⚠️ El campo `password` son las credenciales de acceso a la BD y **solo se
> devuelven en esta respuesta**. El front debe mostrárselas al usuario en ese
> momento (no se pueden recuperar después).

**Errores comunes:**
- `400` → `engine` no soportado o `dbName` inválido.
- `401` → token ausente/inválido.
- `501` → el motor pedido aún no está implementado (`Postgres`/`MySql`/`Mongo`).

### 5.2 Listar mis bases de datos

```
GET /databases
```

**Respuesta `200 OK`:**
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

**Errores comunes:**
- `401` → falta el token, es inválido o expiró.

---

## 6. Ejemplos con `fetch` (JavaScript)

### Registro / Login
```js
async function login(email, password) {
  const res = await fetch("https://localhost:7113/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password })
  });

  if (!res.ok) {
    const err = await res.json();
    throw new Error(err.error ?? "Error de autenticación");
  }

  const auth = await res.json();
  localStorage.setItem("token", auth.token);
  return auth;
}
```

### Petición protegida
```js
async function getMyDatabases() {
  const token = localStorage.getItem("token");
  const res = await fetch("https://localhost:7113/databases", {
    headers: { "Authorization": `Bearer ${token}` }
  });

  if (res.status === 401) {
    // token vencido o ausente → mandar a login
    throw new Error("Sesión expirada");
  }
  return res.json();
}
```

---

## 7. Manejo de errores

### 7.1 Errores de validación (`400`)
Cuando falla la validación del modelo (campos requeridos, formato), la API
devuelve el formato estándar de ASP.NET (`ValidationProblemDetails`):

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

### 7.2 Errores de negocio / autenticación
Los errores controlados (credenciales inválidas, correo duplicado, reglas de los
Stored Procedures) devuelven un formato uniforme:

```json
{
  "status": 401,
  "error": "Credenciales inválidas."
}
```

| Código | Significado |
|--------|-------------|
| `400` | Datos inválidos o regla de negocio incumplida |
| `401` | No autenticado / token inválido / credenciales incorrectas |
| `500` | Error inesperado del servidor |

---

## 8. Estado actual y pendientes (para coordinar)

> Tabla completa y más detallada (todas las rutas, incluidas las de
> infraestructura) en [`docs/routes.md`](routes.md). Detalle de bugs
> encontrados en [`docs/bugs.md`](bugs.md).

| Endpoint | Estado |
|----------|--------|
| `POST /auth/register` | ⚠️ Código completo; depende de `sp_RegisterUser` (no verificable desde este entorno de análisis, ver `routes.md`) |
| `POST /auth/login` | ⚠️ Código completo; depende de `sp_GetLoginByEmail` (ídem) |
| Google / GitHub OAuth | ✅ El callback **ya redirige al frontend** con el token en la query string (Opción A, ver sección 4.3). Pendiente: mover el token fuera de la query string por seguridad (`docs/bugs.md` ítem 1). |
| `POST /databases` (`SqlServer`) | ⚠️ Único motor con provisioner real; depende de `sp_ReserveDatabase`/`sp_ConfirmDatabase`/`sp_FailDatabase` |
| `POST /databases` (`Postgres`/`MySql`/`Mongo`) | ❌ Devuelve `501` a propósito — provisioners son stubs pendientes de implementar |
| `GET /databases` | ⚠️ Código completo; depende de `sp_GetUserDatabases` |

**El login con OAuth en el front ya está resuelto**: se implementó la
**Opción A** (el callback redirige al frontend con el token en query string).
Lo único pendiente es endurecer cómo viaja el token (ver nota de seguridad en
la sección 4.3 y `docs/bugs.md`).
