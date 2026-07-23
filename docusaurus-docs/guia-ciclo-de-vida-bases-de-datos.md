# Guía para el frontend — Ciclo de vida de una base de datos

Cubre los 4 endpoints que se agregan **después** de que una BD ya existe: ver
su detalle (cuando se perdieron los datos de conexión), desactivarla,
eliminarla, y resetear su contraseña si se olvidó. Para la creación
(`POST /databases`) ver la guía **Creación de bases de datos**.

> ⚠️ **Estado actual: código listo, pendiente de desplegar.** Estos 4
> endpoints dependen de 4 Stored Procedures nuevos que todavía no están
> corridos contra la base real, y `reset-password` además depende de una
> cuenta SMTP configurada (`Email` en `appsettings.json`, hoy con
> placeholders). Hasta que el equipo de backend confirme que ambas cosas
> están listas, estas rutas devolverán `500`. Pregunta antes de integrarlas
> en producción.

---

## 1. Resumen de los 4 endpoints

| Método | Ruta | Qué hace | Requiere que la BD esté en estado |
|---|---|---|---|
| `GET` | `/databases/{id}` | Detalle de una BD puntual (host, puerto, usuario — nunca la contraseña) | Cualquiera (mientras no esté `Deleted`) |
| `POST` | `/databases/{id}/deactivate` | Revoca el acceso físico (login/usuario deshabilitado en el motor), sin borrar datos | `Active` |
| `DELETE` | `/databases/{id}` | Borrado físico real e irreversible | `Inactive` |
| `POST` | `/databases/{id}/reset-password` | Genera contraseña nueva y la envía por correo | `Active` |

Los 4 requieren `Authorization: Bearer <token>` y solo operan sobre BDs del
usuario autenticado — un `id` que existe pero es de otro usuario devuelve
`404` (mismo mensaje que "no existe", a propósito, para no revelar IDs
ajenos).

Los 3 que modifican estado (`deactivate`, `DELETE`, `reset-password`)
comparten el rate limit `db-provisioning`: **5 peticiones/min por usuario**
(el mismo cupo que `POST /databases`, no uno adicional — tenlo en cuenta si
tu UI permite encadenar varias de estas acciones rápido).

**Flujo típico** cuando un usuario quiere deshacerse de una BD:

```
GET /databases/{id}          → confirmar cuál es, mostrarle los datos
POST /databases/{id}/deactivate  → un solo clic de "desactivar", con advertencia
DELETE /databases/{id}        → habilitado en la UI SOLO cuando status === "Inactive"
```

No existe (todavía) un endpoint para "reactivar" — trata la desactivación
como un paso serio, no como una pausa trivialmente reversible desde la UI.

---

## 2. `GET /databases/{id}` — Detalle

Para cuando el usuario perdió sus datos de conexión y necesita volver a
verlos (host, puerto, usuario). **Nunca** trae la contraseña — no se puede
recuperar, solo se guarda su hash.

```js
async function getDatabaseDetail(token, databaseId) {
  const res = await fetch(`https://<host>/databases/${databaseId}`, {
    headers: { Authorization: `Bearer ${token}` }
  });

  if (res.status === 404) throw new Error("Base de datos no encontrada.");
  if (!res.ok) throw new Error("Error inesperado.");

  return res.json();
}
```

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

| Campo | Notas |
|---|---|
| `status` | `"Active"`, `"Inactive"` o `"Deleted"` — úsalo para decidir qué botones mostrar (ver sección 5). |
| `host`, `port`, `loginName` | Los mismos datos que se mostraron al crear la BD — el usuario los puede volver a copiar. |
| `password` | **No viene.** Si el usuario la perdió, usa la sección 4 (reset-password). |

**Errores:**
| Código | Causa |
|---|---|
| `401` | Token ausente/inválido/expirado |
| `404` | No existe, o existe pero no es del usuario autenticado |

---

## 3. `POST /databases/{id}/deactivate` — Desactivar

Deshabilita el login/usuario en el motor físico — la BD deja de ser
alcanzable con las credenciales viejas, pero **los datos no se borran**. Es
el paso obligatorio antes de poder eliminar.

```js
async function deactivateDatabase(token, databaseId) {
  const res = await fetch(`https://<host>/databases/${databaseId}/deactivate`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` }
  });

  if (res.status === 429) {
    throw new Error(`Demasiadas solicitudes, reintenta en ${res.headers.get("Retry-After")}s`);
  }
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw new Error(body?.error ?? "Error inesperado");
  }

  return res.json(); // mismo shape que el detalle, con status: "Inactive"
}
```

**UX recomendada:** trátalo como una acción destructiva de primer nivel —
modal de confirmación explícito ("Esto desconectará tu base de datos. No hay
forma de reactivarla desde aquí."), no un toggle casual.

**Errores:**
| Código | Causa | Mensaje |
|---|---|---|
| `400` | La BD no está `Active` (ya estaba inactiva o eliminada) | `"Solo se puede desactivar una base de datos que esté activa."` |
| `401` | Token inválido | — |
| `404` | No existe o no es tuya | `"Base de datos no encontrada."` |
| `429` | Más de 5/min de este usuario | Ver header `Retry-After` |

---

## 4. `DELETE /databases/{id}` — Eliminar

Borrado **físico real** (DROP de la BD y del usuario en el motor) —
irreversible. Solo funciona si la BD ya está `Inactive`.

```js
async function deleteDatabase(token, databaseId) {
  const res = await fetch(`https://<host>/databases/${databaseId}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` }
  });

  if (res.status === 204) return; // éxito, sin body
  if (res.status === 429) {
    throw new Error(`Demasiadas solicitudes, reintenta en ${res.headers.get("Retry-After")}s`);
  }

  const body = await res.json().catch(() => null);
  throw new Error(body?.error ?? "Error inesperado");
}
```

**Importante:** el botón "Eliminar" en la UI debe estar **deshabilitado**
mientras `status !== "Inactive"` — si el usuario intenta eliminar una BD
activa, el backend responde `400` con:

> `"La base de datos debe estar inactiva antes de poder eliminarla. Desactívala primero con POST /databases/{id}/deactivate."`

Muestra ese flujo en dos pasos explícitos en la UI en vez de dejar que el
usuario se encuentre con ese error.

**Errores:**
| Código | Causa | Mensaje |
|---|---|---|
| `400` | La BD no está `Inactive` | Ver arriba |
| `401` | Token inválido | — |
| `404` | No existe o no es tuya | `"Base de datos no encontrada."` |
| `429` | Más de 5/min de este usuario | Ver header `Retry-After` |

---

## 5. `POST /databases/{id}/reset-password` — Olvidé mi contraseña

Genera una contraseña nueva, la aplica en el motor físico y **la envía por
correo** a la dirección con la que el usuario inició sesión. Requiere que la
BD esté `Active`.

```js
async function resetDatabasePassword(token, databaseId) {
  const res = await fetch(`https://<host>/databases/${databaseId}/reset-password`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` }
  });

  if (res.status === 429) {
    throw new Error(`Demasiadas solicitudes, reintenta en ${res.headers.get("Retry-After")}s`);
  }
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw new Error(body?.error ?? "Error inesperado");
  }

  return res.json(); // { status: 200, message: "Se envió la nueva contraseña a tu correo." }
}
```

:::caution Muy importante — no esperes una contraseña en la respuesta
A diferencia de `POST /databases` (donde la contraseña sí viene en el body,
una vez), acá **nunca** viaja en la respuesta HTTP — es intencional, para no
dejarla en el historial de red del navegador ni en logs. Tu UI debe mostrar
un mensaje tipo *"Te enviamos la nueva contraseña a tu correo"*, no un modal
con la contraseña.
:::

**Errores:**
| Código | Causa | Mensaje |
|---|---|---|
| `400` | La BD no está `Active` | `"Solo se puede restablecer la contraseña de una base de datos activa."` |
| `401` | Token inválido, o válido pero sin correo legible | `"El token no contiene un correo válido."` |
| `404` | No existe o no es tuya | `"Base de datos no encontrada."` |
| `429` | Más de 5/min de este usuario | Ver header `Retry-After` |
| `500` | Falla el cambio en el motor, o falla el envío del correo (SMTP caído/mal configurado) | Genérico — informa al usuario que puede reintentar |

---

## 6. Checklist para la UI

- [ ] El botón "Eliminar" solo está habilitado cuando `status === "Inactive"`.
- [ ] Desactivar tiene un modal de confirmación explícito (acción seria, sin
      "reactivar" disponible hoy).
- [ ] "Olvidé mi contraseña" muestra un mensaje de "revisa tu correo", nunca
      espera ni muestra una contraseña en pantalla.
- [ ] Los 3 endpoints de escritura (`deactivate`, `DELETE`, `reset-password`)
      manejan `429` leyendo `Retry-After` — comparten cupo con
      `POST /databases`, así que un usuario que crea y desactiva BDs seguido
      puede toparse con el límite más rápido de lo esperado.
- [ ] El detalle (`GET /databases/{id}`) se usa para refrescar el estado
      después de cada acción, en vez de asumir el `status` optimista del
      lado del cliente.
