
---

## 11. Endpoints de DNS — subdominios (protegidos)

> Sección agregada el 2026-08-12. Va al final y no intercalada entre las de
> bases de datos y estadísticas para no renumerar las secciones 7–10, a las que
> `bugs.md` y `claude.md` ya apuntan por número.

Cada usuario puede crear subdominios propios bajo el espacio de la plataforma:

```
{label}.idempotencia.<zona>       ej: ventas.idempotencia.andrescortes.dev
```

El usuario **solo elige la etiqueta** (`label`); el sufijo lo pone el backend a
partir de su configuración. Los registros se crean como **CNAME** apuntando al
registro raíz de la plataforma, así que si esta cambia de IP se actualiza un
solo registro y todos los subdominios lo siguen solos.

Todos los endpoints requieren `Authorization: Bearer <jwt>`.

**Cuota: 5 subdominios activos por usuario.** Los eliminados no cuentan.

### 11.1 Conocer el dominio base

```http
GET /dns/base-domain
```

```json
{ "baseDomain": "idempotencia.andrescortes.dev" }
```

Úsalo para armar la vista previa del nombre completo mientras el usuario escribe
la etiqueta. **No hardcodees el sufijo en el frontend**: cambia entre ambientes
(local, QA, producción) y quedaría desincronizado sin que nadie se entere.

### 11.2 Crear un subdominio

```http
POST /dns
Content-Type: application/json

{
  "label": "ventas",
  "content": "idempotencia.andrescortes.dev"
}
```

| Campo | Obligatorio | Reglas |
|---|---|---|
| `label` | sí | 3–63 caracteres, solo `a-z`, `0-9` y `-`. No puede empezar ni terminar con guion, ni contener puntos. Se normaliza a minúsculas. |
| `content` | no | Host de destino. Si se omite, se usa el destino por defecto de la plataforma — que es el caso normal. |

Respuesta `201 Created`:

```json
{
  "dnsRecordId": 12,
  "label": "ventas",
  "fqdn": "ventas.idempotencia.andrescortes.dev",
  "recordType": "CNAME",
  "content": "idempotencia.andrescortes.dev",
  "proxied": false,
  "ttl": 1,
  "status": "Active",
  "createdAt": "2026-08-12T15:04:11.120Z"
}
```

`ttl: 1` significa **TTL automático** (lo decide el proveedor), no un segundo.

Errores propios de este endpoint:

| Código | Cuándo |
|---|---|
| `400` | Etiqueta con formato inválido (validación del DTO). |
| `400` | Etiqueta reservada por la plataforma (`www`, `api`, `mail`, `admin`, `db`…). |
| `400` | Se alcanzó la cuota de 5 subdominios. |
| `409` | Ese subdominio ya está en uso — por otro usuario o creado a mano en la zona. |
| `429` | Más de 10 escrituras por minuto (política `dns`). |
| `502` | El proveedor de DNS respondió con un error. Reintentar. |

Un `409` o un `400` de etiqueta son **corregibles por el usuario**: la UI debería
sugerirle probar otra etiqueta, no mostrar un error genérico.

### 11.3 Listar mis subdominios

```http
GET /dns
```

```json
[
  {
    "dnsRecordId": 12,
    "label": "ventas",
    "fqdn": "ventas.idempotencia.andrescortes.dev",
    "recordType": "CNAME",
    "content": "idempotencia.andrescortes.dev",
    "proxied": false,
    "ttl": 1,
    "status": "Active",
    "createdAt": "2026-08-12T15:04:11.120Z",
    "updatedAt": "2026-08-12T15:04:11.120Z",
    "deletedAt": null
  }
]
```

Solo devuelve los subdominios vivos. Los eliminados no aparecen (siguen en la
base para auditoría, pero no son parte del contrato de la API).

### 11.4 Detalle de un subdominio

```http
GET /dns/{id}
```

Mismo objeto que en el listado. `404` si no existe **o si no es del usuario** —
el mismo mensaje para ambos casos, igual que en `/databases/{id}`, para no
revelar qué identificadores ajenos existen.

### 11.5 Actualizar un subdominio

```http
PUT /dns/{id}
Content-Type: application/json

{ "content": "otro-host.andrescortes.dev" }
```

Semántica de actualización **parcial**: los campos que no se envían conservan su
valor actual.

| Campo | Reglas |
|---|---|
| `content` | Host de destino. |
| `ttl` | `1` (automático) o entre `60` y `86400`. Cualquier otro valor → `400`. |
| `proxied` | Ver la advertencia de abajo. |

**El nombre no se puede cambiar.** Eso sería otro subdominio: se borra y se crea
(y así el catálogo conserva la historia de los dos).

Requiere que el subdominio esté `Active`; si no, `400`.

> ⚠️ **Sobre `proxied`.** Con el proxy de Cloudflare activado, el subdominio solo
> enruta HTTP y HTTPS. Si se usa para conectarse a una base de datos por su
> puerto TCP (5432, 3306, 1433, 27017), activarlo lo deja **inservible** — la
> conexión simplemente no llega. Por eso el valor por defecto es `false`.

### 11.6 Eliminar un subdominio

```http
DELETE /dns/{id}
```

`204 No Content`. Elimina el registro en el proveedor y libera el nombre: la
misma etiqueta se puede volver a pedir después, incluso por otro usuario.

A diferencia de las bases de datos, **no hay que desactivarlo primero**. Ese paso
existe en `/databases` para proteger datos del usuario antes de un borrado
irreversible; acá no hay datos que proteger y la operación es reversible
volviendo a crear el subdominio.

La operación es tolerante: si el registro ya no existe en el proveedor (alguien
lo borró a mano desde el panel), igual limpia el catálogo en vez de dejar la
fila atascada.

### 11.7 Notas de propagación

Un subdominio recién creado **no resuelve instantáneamente**. Con TTL automático
suele estar disponible en segundos, pero conviene que la UI no prometa
disponibilidad inmediata. Para verificar:

```bash
dig +short ventas.idempotencia.andrescortes.dev CNAME
```
