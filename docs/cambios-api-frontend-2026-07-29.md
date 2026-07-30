# Cambios en la API — 29 de julio de 2026

Para el equipo de frontend. Tres cambios, ya desplegados en QA. Solo uno rompe
código existente; los otros dos son aditivos.

| # | Cambio | ¿Rompe algo? |
|---|---|---|
| 1 | `POST /auth/login` devuelve un mensaje distinto por cada motivo de fallo | Sí, si comparan contra el texto viejo |
| 2 | Endpoint nuevo: `POST /databases/{id}/reactivate` | No, es aditivo |
| 3 | `currentSizeMB` pasó de ser un valor fijo a un dato real | No, pero cambia cómo debe mostrarse |

La URL base, la autenticación y el formato de las respuestas de error
(`{ "status": ..., "error": ... }`) **no cambiaron**.

---

## 1. `POST /auth/login` — mensajes de error específicos

### Qué cambió

Antes, tres situaciones distintas devolvían el mismo texto
`"Credenciales inválidas."`. Ahora cada una tiene el suyo:

| Situación | HTTP | `error` |
|---|---|---|
| No hay cuenta con ese correo | `401` | `No existe una cuenta registrada con ese correo.` |
| La cuenta se creó con Google/GitHub y no tiene contraseña local | `401` | `Esta cuenta se registró con un proveedor externo (Google o GitHub). Inicia sesión con ese proveedor.` |
| La contraseña no coincide | `401` | `La contraseña es incorrecta.` |
| Credenciales correctas pero cuenta deshabilitada | `401` | `La cuenta está inactiva.` *(sin cambios)* |

El shape de la respuesta es idéntico al de siempre:

```json
{ "status": 401, "error": "La contraseña es incorrecta." }
```

### Qué tienen que hacer

**Si la pantalla de login solo pinta `data.error`, no hay que tocar nada.** El
mensaje correcto aparece solo. Ese es el caso más probable.

**Lo que sí rompe** es cualquier comparación contra el texto viejo. Un
`if (data.error === 'Credenciales inválidas.')` ya no entra nunca y deja al
usuario sin mensaje. Hay que buscarlo y borrarlo.

### Recomendado: ramificar para dar acción, no solo texto

Los cuatro casos piden cosas distintas del usuario, así que vale la pena
aprovecharlos:

- Correo no registrado → link a "Crear cuenta"
- Cuenta de proveedor externo → resaltar los botones de Google/GitHub, y
  preferiblemente deshabilitar el campo de contraseña
- Contraseña incorrecta → link a "Olvidé mi contraseña", dejando el correo ya
  escrito para que solo reintente la clave
- Cuenta inactiva → contacto a soporte, porque el usuario no puede resolverlo
  por su cuenta

**No comparen el string completo** para ramificar: cualquier ajuste de
redacción del backend rompería la pantalla en silencio. Usen una comprobación
tolerante sobre un fragmento estable:

```js
const msg = data.error ?? '';

if (msg.includes('No existe una cuenta'))            mostrarLinkRegistro();
else if (msg.includes('proveedor externo'))          resaltarBotonesOAuth();
else if (msg.includes('contraseña es incorrecta'))   mostrarLinkRecuperar();
else if (msg.includes('cuenta está inactiva'))       mostrarContactoSoporte();

// Siempre pintar el texto, reconocido o no: así los mensajes que no manejamos
// (el 429 de rate limit, un 500) igual le llegan al usuario.
setError(msg);
```

Ese `else` final importa. El `429` sigue existiendo con
`"Demasiadas solicitudes. Inténtalo más tarde."` a los 10 intentos por minuto
desde la misma IP, y con mensajes específicos la gente reintenta más, así que
se va a ver más seguido que antes.

### Sugerencia de UX

Como el mensaje de "cuenta de proveedor externo" ya es explícito, conviene que
los botones de Google/GitHub estén visibles bajo el formulario desde el
principio. Si el usuario tiene que fallar el login para enterarse de que su
cuenta es de Google, el mensaje llegó tarde.

---

## 2. Endpoint nuevo — `POST /databases/{id}/reactivate`

### Para qué es

Deshace un `deactivate`. Hasta ahora desactivar era un camino sin retorno: la
única salida desde `Inactive` era eliminar la base. Eso era una carencia, no
una decisión de diseño — **desactivar nunca borró datos**, solo revocaba la
conexión.

```
POST /databases/{id}/reactivate
Authorization: Bearer <token>
```

Sin body. Rate limit `db-provisioning`: 5 peticiones/min por usuario,
**compartido** con `POST /databases`, `deactivate`, `DELETE` y
`reset-password` (no es un cupo adicional).

### Respuesta `200 OK`

Mismo shape que `GET /databases/{id}`, con `status` en `"Active"` y `pausedAt`
en `null`.

### Errores

| HTTP | Cuándo | `error` |
|---|---|---|
| `400` | La BD no está `Inactive` (sigue activa, o ya fue eliminada) | `Solo se puede reactivar una base de datos que esté inactiva.` |
| `401` | Falta el token, es inválido o expiró | — |
| `404` | El `id` no existe, o existe pero es de otro usuario | `Base de datos no encontrada.` |
| `429` | Más de 5 solicitudes/min de este usuario | `Demasiadas solicitudes. Inténtalo más tarde.` |

El `404` usa el mismo mensaje para "no existe" y "no es tuya" a propósito, para
no revelar si un `id` ajeno existe.

### Ejemplo

```js
async function reactivateDatabase(token, databaseId) {
  const res = await fetch(`${BASE_URL}/databases/${databaseId}/reactivate`, {
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

  return res.json(); // status: "Active", pausedAt: null
}
```

### Cuatro cosas a tener en cuenta

**La contraseña no cambia.** Es lo más fácil de asumir mal. Desactivar no borró
ni rotó nada, así que el estudiante se reconecta con exactamente las mismas
credenciales que ya tenía. No encadenen un `reset-password` después de
reactivar, ni muestren un mensaje tipo "genera una contraseña nueva para
continuar".

**Si falla, no deshabiliten el botón.** La operación es idempotente en los
cuatro motores, así que volver a pulsar es seguro y es la forma prevista de
recuperarse de un fallo a mitad de camino. Puede pasar que el motor ya haya
restaurado el acceso pero el catálogo no alcance a actualizarse; en ese caso la
BD sigue apareciendo `Inactive` y el segundo intento cierra el ciclo. Un botón
deshabilitado tras el error dejaría al usuario atascado sin necesidad.

**Un solo botón a la vez, según `status`:**

| `status` | Botones |
|---|---|
| `"Active"` | "Desactivar" habilitado · "Eliminar" deshabilitado |
| `"Inactive"` | **"Reactivar"** habilitado · "Eliminar" habilitado |
| `"Deleted"` | ninguno |

Nunca "Desactivar" y "Reactivar" juntos.

**Bajarle el tono a la confirmación de desactivar.** La documentación anterior
recomendaba tratarla como acción destructiva de primer nivel, con modal fuerte,
porque no había vuelta atrás. Eso ya no aplica: un toggle de pausar/reanudar es
lo apropiado, con una confirmación ligera del estilo *"tu base dejará de
aceptar conexiones, puedes reactivarla cuando quieras y no pierdes datos"*. El
modal de confirmación fuerte queda reservado para el `DELETE`, que sí es
irreversible.

---

## 3. `currentSizeMB` — ahora es un dato real, pero no en vivo

### Qué cambió

Hasta ahora este campo era ficticio: se fijaba al crear la base y no cambiaba
nunca, por más datos que el estudiante insertara. Ya está corregido — un job en
el backend mide el tamaño real en cada motor y lo sincroniza **cada 15
minutos**.

Aparece en `GET /databases` y en `GET /databases/{id}`. El campo y su tipo no
cambiaron; lo que cambió es que ahora significa algo.

### Qué implica para la UI

**No es tiempo real.** Si el estudiante acaba de cargar datos y refresca, es
normal que todavía vea el valor anterior. Vale la pena un "actualizado
periódicamente" cerca del indicador, y no disparar alertas ni mensajes de error
porque el número no se movió después de una carga.

**Ya se puede dibujar la barra de uso** contra `maxStorageMB`, que antes no
tenía sentido.

**Ojo con las bases de SQL Server.** Ahí la cuota se aplica de verdad en el
motor, así que el estudiante puede toparse con un error de espacio del motor
antes de que el indicador alcance a reflejar que estaba llegando al límite. En
MySQL, PostgreSQL y MongoDB la cuota todavía **no se aplica**: el número sube
pero nadie frena las escrituras.

---

## Resumen de qué tocar

1. Borrar cualquier comparación contra `"Credenciales inválidas."` en el login.
2. *(Opcional pero recomendado)* Ramificar los mensajes de login para dar
   acción, con `includes()`, no con igualdad exacta.
3. Agregar el botón "Reactivar" cuando `status === "Inactive"`.
4. Suavizar el modal de confirmación de "Desactivar".
5. Revisar cómo se presenta `currentSizeMB` para que no parezca tiempo real.

Cualquier duda, escríbannos.
