# Guía para el frontend — Creación de bases de datos

Todo lo que el frontend necesita saber para consumir el flujo de
aprovisionamiento de bases de datos de Colmena: cuándo se crea sola, cuándo
hay que pedirla explícitamente, qué mandar, qué esperar de vuelta, y qué
errores manejar.

> 🆕 Esta guía cubre `POST /databases` y `GET /databases`. El ciclo de vida
> posterior (ver los datos de conexión de nuevo, desactivar, eliminar, y
> resetear la contraseña si se olvida) está en `docs/API.md` §6.3–6.6:
>
> - `GET /databases/{id}` — detalle de una BD (sin password).
> - `POST /databases/{id}/deactivate` — revoca el acceso físico, requiere `Active`.
> - `DELETE /databases/{id}` — borrado real, requiere `Inactive`.
> - `POST /databases/{id}/reset-password` — genera una contraseña nueva y la
>   envía por correo; **la respuesta HTTP no incluye la contraseña**, el
>   frontend debe mostrar "revisa tu correo", no esperar un campo `password`.
>
> ⚠️ Estos 4 endpoints todavía no están desplegados en un ambiente real —
> dependen de Stored Procedures nuevos y de configurar SMTP (ver
> `docs/bugs.md` ítem 19).

---

## 1. Dos formas en que se crea una base de datos

### 1.1 Automática (solo login/registro por contraseña)

La **primera vez** que un usuario se registra (`POST /auth/register`) o
inicia sesión por contraseña (`POST /auth/login`), el backend crea
automáticamente una base de datos **MySQL** a su nombre. El frontend **no
tiene que hacer nada** para esto — viene incluido en la respuesta de login:

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

- `mySqlDatabase` viene poblado **solo la vez que se crea** (primer
  login/registro exitoso del usuario). En logins posteriores es `null`
  porque el usuario ya tiene su BD.
- Si el aprovisionamiento automático falla (motor caído, cuota agotada), el
  login **igual se completa con éxito** (`mySqlDatabase: null`) — la
  autenticación no depende de la infraestructura de bases de datos. El
  usuario puede pedirla manualmente después (sección 2).
- **⚠️ Esto NO aplica a login por OAuth (Google/GitHub).** Ver siguiente
  sección — es la razón por la que existe el paso manual.

### 1.2 Manual (`POST /databases`)

El frontend debe llamar este endpoint explícitamente en dos casos:

1. **Después de un primer login por OAuth** (Google/GitHub) — el
   auto-aprovisionamiento no aplica ahí (se removió deliberadamente: la
   respuesta OAuth viaja por redirect/query string y no hay forma segura de
   entregar ahí una contraseña real; ver `docs/bugs.md` ítem 16).
2. **Para cualquier BD adicional** que el usuario quiera crear (otro motor,
   otro proyecto), sin importar cómo inició sesión.

Patrón recomendado tras resolver un callback OAuth:

```js
async function ensureMySqlDatabaseAfterOAuth(token) {
  // 1. Revisa si el usuario ya tiene una BD MySQL (evita duplicar en logins
  //    posteriores del mismo usuario).
  const existing = await fetch("https://<host>/databases", {
    headers: { Authorization: `Bearer ${token}` }
  }).then(r => r.json());

  const hasMySql = existing.some(db => db.engine === "MySql");
  if (hasMySql) return null;

  // 2. Si no tiene, la pide.
  const res = await fetch("https://<host>/databases", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`
    },
    body: JSON.stringify({ engine: "MySql", dbName: "principal" })
  });

  if (!res.ok) return null; // no bloquear el flujo de login por esto
  return res.json(); // incluye password — mostrarla al usuario AHORA
}
```

---

## 2. `POST /databases` — referencia completa

Requiere `Authorization: Bearer <token>`. Rate limit dedicado:
**5 peticiones/min por usuario** (además del límite global de 100/min/IP) —
crear una BD física conecta al motor real y ejecuta DDL, es costoso.

### Body

```json
{
  "engine": "SqlServer",
  "dbName": "proyecto_ana",
  "maxConcurrentConnections": 10
}
```

| Campo | Tipo | Requerido | Reglas / notas |
|---|---|---|---|
| `engine` | string | Sí | Uno de: `"SqlServer"`, `"Postgres"`, `"MySql"`, `"Mongo"` — **exacto**, respetando mayúsculas/minúsculas tal como está escrito acá. Cualquier otro valor devuelve `400` de validación antes de tocar la base de datos. |
| `dbName` | string | Sí | Solo letras, números y `_`, debe **empezar con una letra**, mínimo 3 y máximo 128 caracteres. Nada de espacios, tildes, comillas, `;`, backticks, etc. — se valida así a propósito (ver sección 9). Es el nombre **lógico**: el backend le antepone automáticamente un prefijo por usuario (ej. `colmena_u12_proyecto_ana`). El frontend **no** arma ese prefijo, solo manda el nombre que el usuario eligió. |
| `maxConcurrentConnections` | number | No | Entero 1–100. Ver sección 4. |

**Validación recomendada en el propio formulario** (antes de mandar el
request, para dar feedback inmediato sin esperar la respuesta del backend):

```js
const DB_NAME_PATTERN = /^[a-zA-Z][a-zA-Z0-9_]{2,127}$/;

function validateDbName(value) {
  if (!DB_NAME_PATTERN.test(value)) {
    return "Debe empezar con una letra y solo puede tener letras, números y guion bajo (mín. 3 caracteres).";
  }
  return null;
}
```

**Importante sobre `dbName`:** como el backend antepone el prefijo, el
frontend debe mostrarle al usuario el `dbName` que viene en la **respuesta**
(ya con el prefijo), no el que mandó en el request — son distintos.

### Respuesta `201 Created`

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

| Campo | Notas para el frontend |
|---|---|
| `databaseId` | Id interno — úsalo si necesitas referenciar esta BD después (no hay endpoint de detalle por id todavía, solo el listado). |
| `dbName`, `host`, `port`, `loginName` | Datos de conexión reales — mostrarlos tal cual, el usuario los necesita para conectarse desde su cliente de BD. |
| `maxStorageMB` | Cuota de almacenamiento aplicada (ver sección 5). |
| `maxConcurrentConnections` | Valor **efectivamente aplicado**, no necesariamente el que pediste (ver sección 4). Vale `0` en `SqlServer`/`Mongo` porque ahí no se aplica. |
| `password` | **Se entrega UNA sola vez.** No se puede recuperar después — el backend solo guarda el hash. Debe mostrarse al usuario en el momento (modal, copiar al portapapeles, etc.) y **no debe** guardarse en el propio backend del frontend ni en logs/analytics. |

---

## 3. Mostrar la contraseña — requisito de UX, no opcional

Tanto en `POST /databases` como en el `mySqlDatabase` del login, la
contraseña real solo viaja **esa vez**. Si el usuario cierra el modal sin
copiarla, no hay forma de recuperarla — hoy no existe un endpoint de "reset
de contraseña" para bases ya aprovisionadas (backlog conocido).

Recomendaciones concretas para el frontend:

- Mostrar la contraseña en un componente que permita copiar (botón
  "Copiar"), no solo mostrarla como texto.
- Advertir explícitamente: *"Esta contraseña no se puede volver a mostrar.
  Guárdala ahora."*
- No enviar la contraseña a ningún servicio de analytics, logging remoto
  (Sentry, LogRocket, etc.) ni al backend propio del frontend.
- No persistirla en `localStorage`/`sessionStorage` más allá de lo necesario
  para mostrarla una vez.

---

## 4. `maxConcurrentConnections` — cómo se resuelve

El frontend puede pedir un valor (1–100) o simplemente omitirlo. El backend
**siempre** decide el valor final así:

1. Si no se pidió nada → usa el default del motor
   (`Provisioning:{Engine}:MaxConcurrentConnections`, hoy `5`).
2. Si se pidió un valor → se acota **siempre** al tope duro del motor
   (`Provisioning:{Engine}:MaxConcurrentConnectionsCap`, hoy `20`). No hay
   forma de pedir más — es un control de abuso, no una preferencia libre.

| Motor | ¿Tiene efecto real? |
|---|---|
| MySQL | ✅ Sí (`MAX_USER_CONNECTIONS`) |
| PostgreSQL | ✅ Sí (`CONNECTION LIMIT`) |
| SQL Server | ❌ No — se acepta el valor pero se ignora (sin equivalente nativo por login todavía) |
| MongoDB | ❌ No — se acepta el valor pero se ignora (sin equivalente nativo por usuario) |

**Implicación para la UI:** si vas a mostrar este campo como configurable en
el formulario de creación, considera ocultarlo o marcarlo como
"solo aplica a MySQL/Postgres" cuando el usuario elija `SqlServer` o `Mongo`,
para no generar expectativas de un control que no se aplica.

---

## 5. Cuota de almacenamiento (`maxStorageMB`)

- **SQL Server**: el motor mismo rechaza escrituras que excedan el tope
  (`MAXSIZE` real en el `CREATE DATABASE`) — es una garantía dura.
- **PostgreSQL / MySQL / MongoDB**: el valor se informa en la respuesta pero
  **no se hace cumplir todavía** contra el motor real (no hay un job de
  monitoreo de cuota implementado). No lo uses como si fuera una garantía
  para estos tres motores — es informativo por ahora.

---

## 6. Listar bases de datos existentes — `GET /databases`

Requiere `Authorization: Bearer <token>`. Sin rate limit dedicado (solo el
global).

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

Puntos importantes:

- **No** incluye `loginName` ni `password` — esas credenciales solo se
  entregan una vez, en el momento de la creación. Si el usuario las perdió,
  hoy no hay forma de regenerarlas desde la API.
- `currentSizeMB` es **informativo, no confiable como fuente de verdad**: no
  hay (todavía) un job que lo sincronice en vivo contra MySQL/Postgres/Mongo.
  No lo uses para bloquear operaciones del lado del frontend (ej. "no puedes
  subir más archivos porque estás al límite") sin verificarlo primero contra
  el motor real.
- Usa este endpoint antes de `POST /databases` para evitar crear duplicados
  del mismo motor sin querer (patrón de la sección 1.2).

---

## 7. Errores a manejar

| Código | Causa | ¿Qué hacer en el frontend |
|---|---|---|
| `400` | Body inválido (`engine`/`dbName` faltantes o mal formados, `maxConcurrentConnections` fuera de 1–100) | Mostrar los errores de `ValidationProblemDetails.errors` campo por campo |
| `400` | `"Motor de base de datos no soportado: '{engine}'."` | No debería pasar si el selector de motor en la UI solo ofrece los 4 valores válidos — validar en el propio formulario antes de mandar |
| `400` | Regla de negocio del catálogo (cuota excedida, nombre duplicado, límite de BDs por usuario, etc. — la decide el SP) | Mostrar `error` tal cual al usuario, es un mensaje pensado para mostrarse |
| `401` | Token ausente, inválido o expirado (cuerpo vacío) | Redirigir a login |
| `401` | `"El token no contiene un identificador de usuario válido."` | Tratar igual que un token inválido — forzar re-login |
| `429` | Más de 5 creaciones/min de este usuario | Leer el header `Retry-After` y mostrar cuánto esperar; no reintentar automáticamente en loop |
| `500` | Falla la creación física en el motor (credenciales admin mal configuradas, motor caído, etc.) | Mensaje genérico — el backend ya revirtió la reserva automáticamente, es seguro dejar que el usuario reintente manualmente |

Patrón de manejo recomendado (igual que el resto de la API):

```js
async function createDatabase(token, { engine, dbName, maxConcurrentConnections }) {
  const res = await fetch("https://<host>/databases", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`
    },
    body: JSON.stringify({ engine, dbName, maxConcurrentConnections })
  });

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

  return res.json(); // CreateDatabaseResponse — mostrar password AHORA (sección 3)
}
```

---

## 8. Por qué `dbName` es tan restrictivo (protección contra SQL injection)

El valor de `dbName` (y `engine`) termina formando parte de sentencias DDL
reales (`CREATE DATABASE`, `CREATE USER`/`CREATE LOGIN`, `GRANT`) contra el
motor físico — no es un valor de negocio común que solo se guarda en una
columna. Por eso el backend lo valida con varias capas:

1. **Formato en el DTO** (`^[a-zA-Z][a-zA-Z0-9_]{2,127}$`): rechaza con `400`
   cualquier cosa que no sea letras/números/guion bajo antes de que el valor
   llegue siquiera a construirse en SQL. Es la primera línea de defensa y la
   razón por la que el frontend **no debería** intentar "arreglar" o escapar
   el valor por su cuenta — simplemente debe replicar esta misma regla en el
   formulario para dar feedback inmediato (ver snippet de la sección 2).
2. **Escape de identificadores por motor** (`QuoteIdentifier` en cada
   provisioner: `[corchetes]` en SQL Server, `` `backticks` `` en MySQL,
   `"comillas dobles"` en Postgres): defensa adicional aunque el valor ya
   pasó el filtro anterior.
3. **Parámetros tipados** (`SqlParameter` / `FromSqlRaw`) para todo lo que
   habla con la base de datos de catálogo — nunca se concatenan strings de
   usuario directamente en una consulta SQL.

Para el frontend, la consecuencia práctica es simple: **replica la regex de
`dbName` en el formulario**, no intentes generar tú un "slug" distinto o
escapar caracteres — si el usuario escribe algo fuera de ese patrón, muéstrale
el error de validación en el momento en vez de intentar sanitizarlo del lado
del cliente.

---

## 9. Checklist rápido para quien implemente el formulario de creación

- [ ] El selector de `engine` solo ofrece los 4 valores soportados
      (`SqlServer`, `Postgres`, `MySql`, `Mongo`).
- [ ] `dbName` se valida en el cliente contra 128 caracteres antes de mandar
      (evita un 400 innecesario), pero la validación real de negocio (cuota,
      duplicados) la hace el backend — no confiar solo en la del cliente.
- [ ] El campo `maxConcurrentConnections` se muestra como opcional / se
      indica que solo aplica a MySQL y Postgres.
- [ ] Tras un `201`, se muestra `password` en un componente copiable con
      advertencia de que no se puede volver a ver, antes de cualquier otra
      navegación.
- [ ] Se usa `GET /databases` para no ofrecer "crear MySQL" a un usuario que
      ya tiene una (o para decidir si hace falta el paso manual post-OAuth).
- [ ] Los errores `429` respetan el header `Retry-After` en vez de
      reintentar en loop.
- [ ] `currentSizeMB` del listado se trata como informativo, no como fuente
      de verdad para bloquear acciones del usuario.
- [ ] `dbName` se valida en el cliente contra la regex
      `^[a-zA-Z][a-zA-Z0-9_]{2,127}$` (letras/números/guion bajo, empieza con
      letra) — no se intenta "limpiar" o escapar el valor del lado del
      cliente, se rechaza y se le pide al usuario corregirlo (ver sección 8).
