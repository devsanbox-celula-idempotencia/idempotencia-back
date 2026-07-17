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
6. Backend responde con el AuthResponse (JSON)
```

Ejemplo desde el front (botón):
```js
// Simplemente navega el navegador; NO uses fetch para iniciar OAuth.
window.location.href = "https://localhost:7113/auth/google/login";
```

> ⚠️ **Nota de integración (leer):** actualmente el paso 6 devuelve el
> `AuthResponse` como **JSON** en el navegador. Para una SPA lo habitual es que
> el backend **redirija de vuelta al frontend** con el token (ej.
> `https://mi-front.com/oauth?token=...`). Si el front lo necesita así,
> coordínalo con backend para ajustar el callback. Ver sección 8.

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
  "dbName": "proyecto_ana"
}
```

**Respuesta `201 Created`:**
```json
{
  "databaseId": 5,
  "dbName": "proyecto_ana",
  "status": "Active",
  "maxStorageMB": 20,
  "loginName": "usr_proyecto_ana",
  "password": "P@ssGeneradaUnaVez"
}
```

> ⚠️ El campo `password` son las credenciales de acceso a la BD y **solo se
> devuelven en esta respuesta**. El front debe mostrárselas al usuario en ese
> momento (no se pueden recuperar después).

### 5.2 Listar mis bases de datos

```
GET /databases
```

**Respuesta `200 OK`:**
```json
[
  {
    "databaseId": 5,
    "dbName": "proyecto_ana",
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

| Endpoint | Estado |
|----------|--------|
| `POST /auth/register` | ✅ Funcional |
| `POST /auth/login` | ✅ Funcional |
| Google / GitHub OAuth | ⚙️ Funcional, pero el callback devuelve JSON (ver sección 4.3) |
| `POST /databases` | ⏳ Depende del SP `sp_CreateDatabase` (pendiente en la DB) |
| `GET /databases` | ⏳ Depende del SP `sp_GetUserDatabases` (pendiente en la DB) |

**Para terminar el login con OAuth en el front**, hay que decidir cómo entregar
el token tras el callback:
- **Opción A (recomendada para SPA):** el callback redirige a una URL del
  frontend con el token (`.../oauth-callback?token=...`), y el front lo lee.
- **Opción B:** el front abre el login OAuth en un popup y el callback hace
  `postMessage` del token a la ventana principal.

Coordina con backend la opción elegida para ajustar el callback.
