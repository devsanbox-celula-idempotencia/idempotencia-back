# API Colmena — Guía de consumo (Frontend)

Documentación para integrar el frontend con el backend de autenticación y
bases de datos de Colmena. Para cada endpoint de negocio encontrarás: body de
entrada, respuesta de éxito, **todos** los errores/excepciones posibles con su
código HTTP exacto, y un ejemplo de consumo.

> Tabla de estado de rutas (funcional/no funcional) en
> [`docs/routes.md`](routes.md). Detalle de bugs y hallazgos de seguridad en
> [`docs/bugs.md`](bugs.md) — esta guía referencia los ítems relevantes de ahí
> en vez de duplicar el detalle técnico.

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

El token contiene los claims `sub`/`UserId`, `email` y `role`
(`Admin`, `Student` o `Developer`), útiles para mostrar/ocultar vistas en el
front — **no** contiene `fullName` (si lo necesitas sin volver a pedirlo,
guárdalo del `AuthResponse` original).

---

## 3. Qué información viaja en cada respuesta (auditoría de datos sensibles)

Antes del detalle por endpoint, un resumen de qué se expone y por qué — para
que el frontend sepa qué es seguro loguear/persistir y qué no:

| Dato | ¿Dónde aparece? | ¿Es sensible? |
|---|---|---|
| `token` (JWT) | `AuthResponse` (JSON) | Sí — es la sesión completa. Nunca lo loguees en consola de producción ni lo mandes a analytics. |
| `userId` | `AuthResponse`, claim del JWT | Bajo riesgo — es tu propio ID, no el de otros. Es un entero secuencial (no UUID), así que en teoría permite estimar cuántos usuarios hay, pero ningún endpoint deja consultar recursos de OTRO `userId` (no hay IDOR conocido). |
| `email`, `fullName`, `role` | `AuthResponse` | Es tu propia información, no la de otros usuarios — ningún endpoint devuelve datos de otro usuario. |
| `mySqlDatabase.password` / `password` en `POST /databases` | Una sola vez, al crearse la BD | **Alta sensibilidad** — es la contraseña real de una BD física. Se entrega UNA vez y no se puede recuperar después (el backend solo guarda el hash). Muéstrala al usuario y no la persistas en tu propio backend/logs. |
| `host`, `port`, `loginName` de la BD | `POST /databases`, `mySqlDatabase` | Es la info de conexión de TU propia BD — necesaria para que puedas conectarte, no expone datos de otros. |
| Datos de otras BDs/usuarios | — | `GET /databases` y `POST /databases` están filtrados por el `userId` del JWT (vía SP); no hay forma de pedir las BDs de otro usuario. |
| Mensajes de error de login | `POST /auth/login` | **Riesgo asumido desde 2026-07-29** — ver `docs/bugs.md` ítem 14, sección "Reversión". El mensaje distingue "correo no registrado", "cuenta OAuth-only" y "contraseña incorrecta", así que un tercero puede confirmar si un correo está registrado mandando `POST /auth/login` con cualquier contraseña. Se aceptó a cambio de claridad para el usuario; lo único que contiene el sondeo es el rate limit de 10 intentos/min por IP. |
| PII en el redirect OAuth | `email`, `fullName`, `role`, `userId`, `token` en la query string | **Hallazgo abierto** — ver `docs/bugs.md` ítems 1 y 15. El navegador guarda esto en su historial y puede quedar en logs de acceso del servidor/proxy. Limpia la URL (`history.replaceState`) apenas la leas (ver sección 4.3). |
| Stack traces / detalles internos de excepciones | — | Nunca se exponen. Todo error no controlado devuelve un mensaje genérico (`"Ocurrió un error inesperado."` o `"Ocurrió un error al procesar la solicitud."`); el detalle real solo queda en los logs del servidor. |
| `PasswordHash` de usuarios | — | Nunca se serializa en ninguna respuesta; se usa solo internamente para verificar con BCrypt. |
| Contraseña nueva de `POST /databases/{id}/reset-password` | Correo electrónico del usuario (SMTP) | **Alta sensibilidad** — a propósito NO viaja en la respuesta HTTP (ver sección 6.6), solo por correo, para no dejarla en historial de red/logs de acceso. El frontend no debe esperar un campo `password` en esta respuesta. |
| `loginName` en `GET /databases/{id}` | `DatabaseDetailResponse` | Bajo riesgo — es tu propio usuario de conexión, no una contraseña. Se reexpone a propósito para el caso de "perdí mis datos de conexión". |

---

## 4. Respuesta de autenticación (`AuthResponse`)

Los flujos de login/registro **por contraseña** devuelven esta estructura:

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
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
| `token` | string | JWT, mándalo en `Authorization: Bearer <token>` en cada request protegido. |
| `expiresAt` | string (ISO 8601, UTC) | Cuándo expira el token — a esa hora, cualquier request protegido devuelve `401`. |
| `userId` | number | Tu propio ID. |
| `email` | string | Tu correo. |
| `fullName` | string | Tu nombre completo. |
| `role` | string | `"Admin"`, `"Student"` o `"Developer"`. |
| `mySqlDatabase` | objeto o `null` | Ver sección 4.1 — solo viene poblado la primera vez que se crea tu BD MySQL automática. |

### 4.1 Aprovisionamiento automático de BD MySQL (solo login/registro por contraseña)

**La primera vez** que un usuario se registra o inicia sesión **por
contraseña**, el backend crea automáticamente una base de datos MySQL a su
nombre — no requiere llamar `POST /databases`. Esa única vez, `mySqlDatabase`
viene poblado:

```json
{
  "token": "...",
  "expiresAt": "...",
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

> ⚠️ Igual que en `POST /databases` (sección 6.1), `password` **solo se
> entrega esta vez** — el front debe mostrarla al usuario en el momento (no se
> puede recuperar después). En logins posteriores, `mySqlDatabase` es `null`
> porque el usuario ya tiene su BD; para ver sus BDs existentes usa
> `GET /databases`.

Si el aprovisionamiento automático falla (el motor MySQL no responde, se
agotó una cuota, etc.), el login **igual se completa con éxito**
(`mySqlDatabase: null`) — la autenticación no depende de la infraestructura de
bases de datos. El usuario puede reintentar manualmente con `POST /databases`.

> ⚠️ **Esto NO aplica a login por OAuth (Google/GitHub)** — ver sección 4.4:
> el frontend tiene que llamar `POST /databases` explícitamente en ese caso.

---

## 5. Endpoints de autenticación

### 5.1 Registro por contraseña

```
POST /auth/register
```
Anónimo · rate limit `auth` (10/min/IP)

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "Segura123",
  "fullName": "Ana Pérez"
}
```

**Reglas de validación:**
- `email`: requerido, formato email válido (`algo@dominio.algo`, sin
  espacios), máx. 150 caracteres. Se normaliza automáticamente a minúsculas
  y sin espacios al inicio/final antes de validarse y guardarse — un mismo
  correo con mayúsculas distintas no crea cuentas duplicadas.
- `password`: requerido, mín. 8 y **máx. 12 caracteres**. No se recorta ni se
  normaliza (los espacios, si los escribiste, son parte de la contraseña).
- `fullName`: requerido, máx. 150 caracteres. Solo letras (con acentos/ñ),
  espacios, apóstrofes, guiones y puntos — sin dígitos ni símbolos. Los
  espacios repetidos se colapsan a uno solo automáticamente.

**Respuesta `200 OK`:** un `AuthResponse` (sección 4), con `mySqlDatabase`
poblado (primer login del usuario, siempre — es su registro).

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje exacto |
|---|---|---|
| `400` | Body inválido (ver sección 8.1) | `ValidationProblemDetails` estándar de ASP.NET |
| `400` | El correo ya está registrado (regla del SP `sp_RegisterUser`) | El texto lo define el SP con `THROW`/`RAISERROR`; ejemplo visto en pruebas: `"El correo ya está registrado."` |
| `429` | Más de 10 registros/min desde la misma IP | `"Demasiadas solicitudes. Inténtalo más tarde."` |
| `500` | Error inesperado (motor SQL caído, etc.) | `"Ocurrió un error al procesar la solicitud."` (genérico, sin detalle interno) |

---

### 5.2 Login por contraseña

```
POST /auth/login
```
Anónimo · rate limit `auth` (10/min/IP)

**Body:**
```json
{
  "email": "ana@uni.edu",
  "password": "Segura123"
}
```

**Respuesta `200 OK`:** un `AuthResponse` (sección 4). `mySqlDatabase` viene
poblado solo si es la primera vez que este usuario se autentica por
contraseña (no tenía BD MySQL todavía); si no, es `null`.

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje exacto |
|---|---|---|
| `400` | Body inválido (email mal formado, password vacío) | `ValidationProblemDetails` estándar |
| `401` | No hay ninguna cuenta con ese correo | `"No existe una cuenta registrada con ese correo."` |
| `401` | La cuenta existe pero se creó por Google/GitHub y no tiene contraseña local | `"Esta cuenta se registró con un proveedor externo (Google o GitHub). Inicia sesión con ese proveedor."` |
| `401` | El correo existe y tiene contraseña local, pero no coincide | `"La contraseña es incorrecta."` |
| `401` | Credenciales correctas pero la cuenta está inactiva (`IsActive = false`) | `"La cuenta está inactiva."` |
| `429` | Más de 10 intentos/min desde la misma IP | `"Demasiadas solicitudes. Inténtalo más tarde."` |
| `500` | Error inesperado | `"Ocurrió un error al procesar la solicitud."` |

> **Cambio 2026-07-29:** hasta esta fecha los tres primeros casos devolvían un
> único mensaje `"Credenciales inválidas."` para no revelar qué correos están
> registrados. Por decisión de producto ahora cada caso tiene mensaje propio
> (ver `bugs.md` ítem 14, sección "Reversión"). Si tu frontend hacía
> `if (error === 'Credenciales inválidas.')`, ese `if` ya no entra nunca.
> **No compares el texto exacto**: encadena por `status` y usa el `error` como
> texto a mostrar; si necesitas ramificar (p. ej. mostrar los botones de
> Google/GitHub), haz una comprobación tolerante (`error.includes('proveedor
> externo')`) para que un ajuste de redacción no rompa la pantalla.

---

### 5.3 Login con Google / GitHub (OAuth)

El flujo OAuth lo maneja **el backend**. El frontend solo necesita **redirigir el
navegador** a los endpoints de inicio:

| Proveedor | URL para iniciar el login |
|-----------|---------------------------|
| Google | `https://localhost:7113/auth/google/login` |
| GitHub | `https://localhost:7113/auth/github/login` |

> **Rate limit:** los 4 endpoints OAuth (login y callback de Google/GitHub)
> usan la política `oauth` (**20 peticiones/min por IP**), agregada el
> 2026-07-23 (`bugs.md` ítem 2). Un login completo consume 2 (el `/login` que
> redirige y el `/callback` que vuelve), así que 20/min deja margen para
> reintentos legítimos; al superarlo se responde `429` como el resto de la API.

**Flujo completo:**

```
1. Front redirige el navegador a  /auth/google/login
2. Backend redirige a Google (pantalla de consentimiento)
3. Usuario acepta
4. Google → backend (/signin-google, interno)
5. Backend resuelve el usuario y firma el JWT
6. Backend redirige al frontend con los datos en la query string
```

Ejemplo desde el front (botón):
```js
// Simplemente navega el navegador; NO uses fetch para iniciar OAuth.
window.location.href = "https://localhost:7113/auth/google/login";
```

**El paso 6 redirige al frontend** así:

```
{Frontend:BaseUrl}/oauth/callback?token=...&expiresAt=...&userId=...&email=...&fullName=...&role=...
```

En caso de error, redirige con `?error=<mensaje>` en su lugar (mensajes
posibles: `"No se pudo completar la autenticación externa."`,
`"El proveedor externo no entregó los datos mínimos (id/email)."`, o el
genérico `"Ocurrió un error inesperado."`). `Frontend:BaseUrl` se configura en
`appsettings.json` (hoy `http://localhost:5555` en desarrollo). El front debe
implementar la ruta `/oauth/callback` para leer esos query params.

> ⚠️ **Nota de seguridad (`bugs.md` ítems 1 y 15):** el token JWT **y además**
> `email`, `fullName`, `role`, `userId` viajan como parámetros de **query
> string** (no en el fragmento `#` ni por POST), lo que los expone al
> historial del navegador, logs de acceso del servidor/proxy, y el header
> `Referer` si la página de callback carga cualquier recurso de terceros. Es
> un hallazgo abierto (pendiente moverlo a un intercambio de código de un solo
> uso). **Mientras no se corrija, el frontend DEBE:**
> 1. Leer los query params apenas carga `/oauth/callback`.
> 2. Guardar el token (memoria/localStorage) igual que en login por contraseña.
> 3. Llamar **inmediatamente** `history.replaceState(null, "", "/oauth/callback")`
>    (o navegar a otra ruta) para que esos valores no queden en el historial
>    del navegador ni se reenvíen si el usuario comparte la URL.

### 5.4 OAuth auto-aprovisiona la BD MySQL y envía las credenciales por correo

Igual que el login por contraseña (sección 4.1), el login por OAuth **sí** crea
automáticamente la BD MySQL del usuario la primera vez. La diferencia es **cómo**
se entregan las credenciales: como la respuesta de OAuth viaja por
redirect/query string (donde no es seguro meter una contraseña real — ver la
nota de seguridad de arriba y `bugs.md` ítem 16), el backend envía las
credenciales completas (host, puerto, base de datos, usuario y contraseña) al
**correo** del usuario. **El frontend no necesita hacer nada** para la BD
"principal" tras un login OAuth: no hay que llamar `POST /databases`.

Detalles a tener en cuenta:

- La contraseña NO viene en el redirect ni en ninguna respuesta HTTP del login
  OAuth — solo en el correo. Es un secreto de un solo uso.
- El envío es best-effort: si el correo llegara a fallar, el login igual
  funciona y la BD queda creada; el usuario puede regenerar la contraseña con
  `POST /databases/{id}/reset-password` (sección 6.6), que también la manda por
  correo.
- `POST /databases` sigue disponible para crear BDs adicionales o de otros
  motores (sección 6.1).

---

## 6. Endpoints de bases de datos (protegidos)

Requieren header `Authorization: Bearer <token>`.

### 6.1 Crear (aprovisionar) una base de datos

```
POST /databases
```
JWT Bearer · rate limit `db-provisioning` (**5/min por usuario**, además del
límite global de 100/min/IP) — crear una BD física es costoso y no debe poder
repetirse en bucle.

**Body:**
```json
{
  "engine": "SqlServer",
  "dbName": "proyecto_ana",
  "maxConcurrentConnections": 10
}
```

| Campo | Requerido | Notas |
|---|---|---|
| `engine` | Sí | `"SqlServer"`, `"Postgres"`, `"MySql"` o `"Mongo"` — valor exacto, sensible a mayúsculas/minúsculas. Los 4 tienen provisioner real (creación de usuario/rol + BD + permisos acotados a esa BD). Máx. 20 caracteres. |
| `dbName` | Sí | Solo letras, números y guion bajo, debe **empezar con una letra** y tener al menos 3 caracteres (máx. 128). Sin espacios ni símbolos — se valida así a propósito para no arriesgar caracteres raros en la construcción del DDL de cada motor. El backend le antepone un prefijo por usuario (ej. `colmena_u12_...`). |
| `maxConcurrentConnections` | No | Entero 1-100. Si se omite, usa el default del motor (hoy 5). El backend SIEMPRE lo acota a un tope duro por motor (hoy 20) sin importar lo que pidas. Solo tiene efecto real en **MySQL** y **Postgres**; en **SqlServer**/**Mongo** se ignora (`bugs.md` ítem 12). |

> No necesitas llamar este endpoint para tu primera BD MySQL: si entraste por
> contraseña llega en la respuesta (sección 4.1), y si entraste por OAuth llega
> por correo (sección 5.4). Úsalo para BDs adicionales o de otro motor.

**Respuesta `201 Created`:**
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

> `maxConcurrentConnections` en la respuesta es el valor **efectivamente
> aplicado** (ya acotado al cap), puede diferir de lo pedido. Vale `0` en
> SqlServer/Mongo porque ahí no se aplica.
>
> ⚠️ `password` son las credenciales de acceso a la BD y **solo se devuelven
> en esta respuesta**. Muéstraselas al usuario en ese momento — no se pueden
> recuperar después (el backend solo guarda el hash).

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje exacto |
|---|---|---|
| `400` | Body inválido (`engine`/`dbName` faltantes, `maxConcurrentConnections` fuera de 1-100) | `ValidationProblemDetails` estándar |
| `400` | `engine` no es uno de los 4 soportados | `"Motor de base de datos no soportado: '{engine}'."` |
| `400` | Regla de negocio del catálogo (cuota excedida, nombre duplicado, etc. — la decide `sp_ReserveDatabase`) | Mensaje definido por el SP |
| `401` | Token ausente, inválido o expirado | — (respuesta estándar de `[Authorize]`) |
| `401` | Token válido pero sin claim `UserId` legible | `"El token no contiene un identificador de usuario válido."` |
| `429` | Más de 5 creaciones/min de este usuario | `"Demasiadas solicitudes. Inténtalo más tarde."` |
| `500` | Falla la creación física en el motor (credenciales admin mal configuradas, motor caído, etc.) — el backend revierte la reserva automáticamente | `"Ocurrió un error al procesar la solicitud."` o `"Ocurrió un error inesperado."` según el tipo de excepción; nunca el detalle interno |

### 6.2 Listar mis bases de datos

```
GET /databases
```
JWT Bearer

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

Nota: **no** incluye `loginName` ni `password` — esas credenciales solo se
entregan una vez, en el momento de la creación (sección 6.1 / 4.1). Si el
usuario las perdió, hoy no existe un endpoint para regenerarlas (ver
`bugs.md`, backlog).

> ℹ️ **Sobre `currentSizeMB` — léelo antes de dibujar una barra de uso.**
> Hasta el 2026-07-29 este campo era ficticio: se fijaba al crear la BD y no
> cambiaba nunca. Ya está corregido (`bugs.md` ítem 25): un job en el backend
> mide el tamaño real en cada motor y lo sincroniza **cada 15 minutos**.
>
> Lo que eso implica para la UI: el dato es real pero **no es instantáneo**. Si
> el estudiante acaba de cargar datos y refresca, es normal que todavía vea el
> valor anterior. No presentes el número como si fuera en vivo — sirve un
> "actualizado periódicamente" cerca del indicador, y no dispares alertas
> basadas en que el valor no se movió tras una carga.
>
> Ojo con SQL Server: ahí la cuota **sí** se aplica de verdad en el motor
> (`MAXSIZE` en el `CREATE DATABASE`), así que el estudiante puede toparse con
> un error de espacio del motor antes de que el indicador alcance a reflejar
> que estaba llegando al límite. Aplicar la cuota en MySQL/Postgres/Mongo sigue
> pendiente (`bugs.md` ítem 10): ahí el número sube pero nadie lo frena.
>
> Mientras el fix no esté desplegado en el ambiente que estés consumiendo, el
> campo se comporta como antes (siempre el mismo valor).

**Errores/excepciones posibles:**
| Código | Cuándo |
|---|---|
| `401` | Falta el token, es inválido o expiró |

---

### 6.3 Detalle de una base de datos

```
GET /databases/{id}
```
JWT Bearer

Pensado para cuando el usuario perdió sus datos de conexión (host, puerto,
usuario) y necesita volver a verlos. **Nunca** incluye `password` — no se
puede recuperar (el backend solo guarda el hash); para eso existe la sección
6.6 (reset de contraseña).

**Respuesta `200 OK`:**
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

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje |
|---|---|---|
| `401` | Falta el token, es inválido o expiró | — |
| `404` | El `id` no existe, o existe pero es de otro usuario | `"Base de datos no encontrada."` — mismo mensaje para ambos casos a propósito, para no revelar si un ID ajeno existe |

---

### 6.4 Desactivar una base de datos

```
POST /databases/{id}/deactivate
```
JWT Bearer · rate limit `db-provisioning` (5/min/usuario)

Revoca el acceso físico (deshabilita el login/usuario en el motor) **sin
borrar los datos**. Requiere que la BD esté `Active`. Es el paso obligatorio
antes de poder eliminarla (sección 6.5) — hoy no hay un endpoint para
reactivarla, trátalo como una confirmación intermedia antes del borrado
definitivo, no como una acción trivialmente reversible desde la API.

**Respuesta `200 OK`:** el mismo shape que la sección 6.3, con
`"status": "Inactive"` y `pausedAt` poblado.

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje |
|---|---|---|
| `401` | Falta el token, es inválido o expiró | — |
| `404` | El `id` no existe o no es del usuario | `"Base de datos no encontrada."` |
| `400` | La BD no está `Active` (ya inactiva o eliminada) | `"Solo se puede desactivar una base de datos que esté activa."` |
| `429` | Más de 5 solicitudes/min de este usuario (comparte el límite con `POST /databases`) | `"Demasiadas solicitudes. Inténtalo más tarde."` |

---

### 6.5 Eliminar una base de datos

```
DELETE /databases/{id}
```
JWT Bearer · rate limit `db-provisioning` (5/min/usuario)

Borrado **físico real** (DROP de la BD y del login/usuario en el motor) —
irreversible. Solo permitido si la BD ya está `Inactive` (sección 6.4).

**Respuesta `204 No Content`** (sin body) si se eliminó correctamente.

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje |
|---|---|---|
| `401` | Falta el token, es inválido o expiró | — |
| `404` | El `id` no existe o no es del usuario | `"Base de datos no encontrada."` |
| `400` | La BD no está `Inactive` | `"La base de datos debe estar inactiva antes de poder eliminarla. Desactívala primero con POST /databases/{id}/deactivate."` |
| `429` | Más de 5 solicitudes/min de este usuario | `"Demasiadas solicitudes. Inténtalo más tarde."` |

---

### 6.6 Restablecer la contraseña de una base de datos

```
POST /databases/{id}/reset-password
```
JWT Bearer · rate limit `db-provisioning` (5/min/usuario)

Para cuando el usuario perdió/olvidó la contraseña de su BD (no se puede
recuperar la anterior — solo se guarda el hash). Genera una contraseña nueva,
la aplica en el motor físico y **la envía por correo** a la dirección
registrada del usuario. Requiere que la BD esté `Active`.

> ⚠️ A diferencia de `POST /databases` y del login, **la contraseña nueva NO
> viaja en la respuesta HTTP** — solo llega por correo. Es intencional: evita
> que quede en el historial de red del navegador o en logs de acceso. El
> frontend debe mostrar un mensaje tipo "revisa tu correo", no esperar un
> campo `password` en la respuesta.

**Respuesta `200 OK`:**
```json
{ "status": 200, "message": "Se envió la nueva contraseña a tu correo." }
```

**Errores/excepciones posibles:**
| Código | Cuándo | Mensaje |
|---|---|---|
| `401` | Falta el token, es inválido o expiró | — |
| `401` | Token válido pero sin claim de correo legible | `"El token no contiene un correo válido."` |
| `404` | El `id` no existe o no es del usuario | `"Base de datos no encontrada."` |
| `400` | La BD no está `Active` | `"Solo se puede restablecer la contraseña de una base de datos activa."` |
| `429` | Más de 5 solicitudes/min de este usuario | `"Demasiadas solicitudes. Inténtalo más tarde."` |
| `500` | Falla el cambio físico en el motor, o falla el envío del correo (SMTP mal configurado/caído) — en ambos casos la operación se aborta o el usuario debe reintentar | genérico, sin detalle interno |

---

## 7. Estadísticas de la plataforma (solo Admin)

```
GET /statistics
```
JWT Bearer, requiere claim `role = "Admin"`

**Respuesta `200 OK`:**
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

Son solo conteos agregados de toda la plataforma — no expone datos
individuales de ningún usuario ni de ninguna BD en particular.

**Errores/excepciones posibles:**
| Código | Cuándo |
|---|---|
| `401` | Token ausente, inválido o expirado |
| `403` | Token válido pero el usuario no tiene rol `Admin` |

---

## 8. Manejo de errores

### 8.1 Errores de validación (`400`)
Cuando falla la validación del modelo (campos requeridos, formato, rangos),
la API devuelve el formato estándar de ASP.NET (`ValidationProblemDetails`):

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

### 8.2 Errores de negocio / autenticación / infraestructura
Todo lo demás (credenciales inválidas, correo duplicado, reglas de los
Stored Procedures, rate limit excedido, errores inesperados) devuelve un
formato uniforme:

```json
{
  "status": 401,
  "error": "Credenciales inválidas."
}
```

| Código | Significado | ¿El mensaje es seguro de mostrar al usuario tal cual? |
|--------|-------------|---|
| `400` | Datos inválidos o regla de negocio incumplida | Sí — estos mensajes están escritos para mostrarse. |
| `401` | No autenticado / token inválido / credenciales incorrectas | Sí. |
| `403` | Autenticado pero sin el rol requerido | Sí. |
| `429` | Rate limit excedido | Sí — además viene el header `Retry-After` con los segundos a esperar. |
| `500` | Error inesperado del servidor | Sí, pero es deliberadamente genérico (nunca incluye detalle interno/stack trace — eso solo queda en logs del backend). No hay nada más específico que extraer de un 500. |

**Patrón recomendado para consumir cualquier endpoint:**

```js
async function apiCall(url, options = {}) {
  const res = await fetch(url, options);

  if (res.status === 429) {
    const retryAfter = res.headers.get("Retry-After");
    throw new Error(`Demasiadas solicitudes, reintenta en ${retryAfter}s`);
  }

  if (!res.ok) {
    const body = await res.json().catch(() => null);
    // ValidationProblemDetails trae "errors"; el resto trae "error"
    const message = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : (body?.error ?? "Error inesperado");
    throw new Error(message);
  }

  return res.status === 201 || res.status === 200 ? res.json() : null;
}
```

---

## 9. Ejemplos con `fetch` (JavaScript)

### Registro / Login por contraseña
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

  if (auth.mySqlDatabase) {
    // Primera vez de este usuario: muéstrale la contraseña AHORA, no se
    // puede recuperar después.
    showDatabaseCredentialsOnce(auth.mySqlDatabase);
  }

  return auth;
}
```

### Callback OAuth
```js
// En la ruta /oauth/callback de tu frontend:
async function handleOAuthCallback() {
  const params = new URLSearchParams(window.location.search);

  if (params.has("error")) {
    throw new Error(params.get("error"));
  }

  const token = params.get("token");
  localStorage.setItem("token", token);

  // Limpiar la URL cuanto antes (nota de seguridad, sección 5.3).
  window.history.replaceState(null, "", "/oauth/callback");

  // La BD MySQL "principal" se auto-aprovisiona en el backend y sus credenciales
  // llegan por correo (sección 5.4) — el front no tiene que pedirla. Usar
  // POST /databases solo para BDs adicionales o de otro motor.
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

## 10. Estado actual y pendientes (para coordinar)

> Tabla completa y más detallada (todas las rutas, incluidas las de
> infraestructura) en [`docs/routes.md`](routes.md). Detalle de bugs
> encontrados en [`docs/bugs.md`](bugs.md).

| Endpoint | Estado |
|----------|--------|
| `POST /auth/register` | ⚠️ Código completo; depende de `sp_RegisterUser` (confirmado en vivo funcionando, sin el bug de `Roles` — `bugs.md` ítem 9) |
| `POST /auth/login` | ✅ Confirmado en vivo funcionando. Enumeración de cuentas OAuth-only corregida (`bugs.md` ítem 14). |
| Google / GitHub OAuth | ⚠️ Funcional (rate limit `oauth` 20/min/IP agregado — `bugs.md` ítem 2). El `redirect_uri_mismatch` de QA quedó resuelto y confirmado (`bugs.md` ítem 17). Auto-aprovisiona la BD MySQL y envía las credenciales por correo (`bugs.md` ítem 16) — el front no necesita pedirla (sección 5.4). Todavía expone PII + token en query string (`bugs.md` ítems 1 y 15, abiertos) — el front debe limpiar la URL (sección 5.3). |
| `POST /databases` (los 4 motores) | ⚠️ Los 4 provisioners están implementados; depende de `sp_ReserveDatabase`/`sp_ConfirmDatabase`/`sp_FailDatabase`. Rate limit dedicado (5/min/usuario) y `maxConcurrentConnections` configurable agregados. |
| Auto-aprovisionamiento MySQL en primer login | ✅ Implementado en los tres flujos: register/login por contraseña (credenciales en el JSON) y OAuth (credenciales por correo — `bugs.md` ítem 16). |
| `GET /databases` | ⚠️ Código completo; depende de `sp_GetUserDatabases`. `currentSizeMB` ya refleja el tamaño real, sincronizado cada 15 min por un job (`bugs.md` ítem 25) — no es un valor en vivo. |
| `GET /databases/{id}` (detalle) | ✅ Desplegado y confirmado en vivo (2026-07-23) — `sp_GetDatabaseDetail` ya está en la BD real (`bugs.md` ítem 19, 🟢). |
| `POST /databases/{id}/deactivate` | ✅ Desplegado y confirmado (2026-07-23) — `sp_DeactivateDatabase` en la BD real. Revoca acceso físico sin borrar datos. |
| `DELETE /databases/{id}` | ✅ Desplegado y confirmado (2026-07-23) — `sp_DeleteDatabase` en la BD real. Borrado físico real, solo si la BD está `Inactive`. |
| `POST /databases/{id}/reset-password` | ✅ Desplegado y confirmado (2026-07-23) — `sp_ResetDatabasePassword` en la BD real y SMTP configurado; la contraseña nueva llega por correo. |
| `GET /statistics` (solo Admin) | ⚠️ Código completo; depende de `sp_GetPlatformStatistics`, no verificado en vivo todavía. |

**Aislamiento entre usuarios en el motor físico** (relevante si el frontend
alguna vez conecta directo a las BDs, no solo vía esta API): SQL Server y
Postgres ya restringen qué bases puede ver/a cuáles conectarse un usuario
recién creado; MySQL tiene una limitación conocida del motor (nombres de
otras BDs visibles, pero no sus datos) — detalle en `bugs.md` ítem 13.
