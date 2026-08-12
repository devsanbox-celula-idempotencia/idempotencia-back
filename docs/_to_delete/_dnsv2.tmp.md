
---

## 11. Subdominios DNS — autoservicio (protegidos)

> Sección agregada el 2026-08-12. Va al final y no intercalada entre las de
> bases de datos y estadísticas para no renumerar las secciones 7–10, a las que
> `bugs.md` y `claude.md` ya apuntan por número.
>
> Guía completa (incluido el procedimiento administrativo) en
> `docusaurus-docs/08-dns-subdominios.md`.

Cada usuario puede crear subdominios propios para sus proyectos:

```
[nombre-elegido].[celula].coderhivex.com     ej: airflow.datos.coderhivex.com
```

El **nombre** lo define el usuario, la **célula** es su equipo de trabajo, y el
dominio lo pone el backend. Se crea un registro **A proxeado** hacia la IPv4
pública que aporta el usuario; el HTTPS lo resuelve Cloudflare automáticamente.

Todos los endpoints requieren `Authorization: Bearer <jwt>`.

**Cuota: 5 subdominios vivos por usuario.** Los eliminados y revocados no cuentan.

### 11.1 Conocer la zona

```http
GET /dns/zone
```

```json
{ "zoneName": "coderhivex.com", "pattern": "{label}.{celula}.coderhivex.com" }
```

Úsalo para la vista previa del nombre completo mientras el usuario escribe. **No
hardcodees el dominio**: cambia entre ambientes y quedaría desincronizado sin que
nadie se entere.

### 11.2 Crear un subdominio

```http
POST /dns
Content-Type: application/json

{
  "label": "airflow",
  "cell": "datos",
  "ipAddress": "203.0.113.10"
}
```

| Campo | Obligatorio | Reglas |
|---|---|---|
| `label` | sí | 3–63 caracteres, solo `a-z`, `0-9` y `-`. No puede empezar ni terminar con guion, ni contener puntos. Se normaliza a minúsculas. |
| `cell` | sí | Mismas reglas que `label`. |
| `ipAddress` | sí | IPv4 **pública**. |

Respuesta `201 Created`:

```json
{
  "dnsRecordId": 12,
  "label": "airflow",
  "cell": "datos",
  "fqdn": "airflow.datos.coderhivex.com",
  "recordType": "A",
  "ipAddress": "203.0.113.10",
  "proxied": true,
  "ttl": 1,
  "status": "Active",
  "createdAt": "2026-08-12T15:04:11.120Z",
  "updatedAt": "2026-08-12T15:04:11.120Z"
}
```

**La IP tiene que ser alcanzable desde internet.** Se rechazan rangos privados
(`10.x`, `172.16–31.x`, `192.168.x`), loopback (`127.x`), link-local
(`169.254.x`) y **CGNAT (`100.64.x`–`100.127.x`, el rango de Tailscale)**. El
registro va proxeado, así que quien se conecta al origen es el borde de
Cloudflare desde internet: una IP privada es válida como IPv4 y perfectamente
inalcanzable desde ahí, y el subdominio se crearía sin error para fallar después
con un 522 que no explica nada.

Errores propios de este endpoint:

| Código | Cuándo |
|---|---|
| `400` | `label` o `cell` con formato inválido. |
| `400` | `label` o `cell` reservados (`www`, `api`, `mail`, `admin`, `db`…). |
| `400` | La IP no es pública. |
| `400` | Se alcanzó la cuota de 5 subdominios. |
| `409` | Ese subdominio ya está en uso. |
| `429` | Más de 10 escrituras por minuto (política `dns`). |
| `502` | El proveedor de DNS respondió con error. Reintentar. |

Un `409` o un `400` de nombre son **corregibles por el usuario**: la UI debería
sugerirle probar otro nombre, no mostrar un error genérico.

### 11.3 Listar y ver

```http
GET /dns          # mis subdominios vivos
GET /dns/{id}     # detalle
```

`404` si no existe **o si no es del usuario** — el mismo mensaje para ambos
casos, igual que en `/databases/{id}`, para no revelar qué identificadores ajenos
existen.

### 11.4 Reapuntar a otra IP

```http
PUT /dns/{id}
Content-Type: application/json

{ "ipAddress": "203.0.113.99" }
```

**El nombre no se puede cambiar** (eso sería otro subdominio: se borra y se
crea), y **el body no acepta `proxied` ni `ttl`**: los fija la plataforma porque
de ellos depende el certificado (§11.6). Requiere estado `Active`.

### 11.5 Eliminar

```http
DELETE /dns/{id}
```

`204 No Content`. Libera el nombre: la misma combinación `label` + `cell` se
puede volver a pedir, incluso por otra persona.

A diferencia de las bases de datos, **no hay que desactivarlo primero**. Ese paso
existe en `/databases` para proteger datos antes de un borrado irreversible; acá
no hay datos que proteger y la operación es reversible volviendo a crear el
subdominio.

Es tolerante: si el registro ya no existe en el proveedor (alguien lo borró a
mano en el panel), igual limpia el catálogo en vez de dejar la fila atascada.

### 11.6 HTTPS y propagación

El certificado se emite solo, pero **depende de que la zona tenga Advanced
Certificate Manager con Total TLS activado**. El comodín gratuito de Cloudflare
cubre `*.coderhivex.com` —un solo nivel— y estos nombres tienen dos, así que sin
ACM el subdominio resuelve pero da error de certificado. Detalle y el `curl` para
activarlo, en `docusaurus-docs/08-dns-subdominios.md`.

Como el registro está proxeado, `dig` devuelve **IPs de Cloudflare, no la del
usuario**. Es correcto. Y el proxy solo enruta HTTP/HTTPS: estos subdominios no
sirven para exponer un puerto TCP arbitrario.

La propagación no es instantánea (segundos, con TTL automático). La UI no debería
prometer disponibilidad inmediata.

```bash
dig +short airflow.datos.coderhivex.com
curl -sI https://airflow.datos.coderhivex.com | head -1
```

---

## 12. Administración de subdominios (solo Admin)

Requieren `Authorization: Bearer <jwt>` con rol `Admin`: token inválido o
expirado → `401`, usuario sin el rol → `403`.

### 12.1 Auditar el inventario

```http
GET /admin/dns
```

Sin filtros devuelve todos los subdominios **vivos** de todos los usuarios,
del más antiguo al más reciente. Cada fila agrega el dueño y la antigüedad:

```json
[
  {
    "dnsRecordId": 12,
    "userId": 7,
    "userEmail": "ana@coderhivex.com",
    "label": "airflow",
    "cell": "datos",
    "fqdn": "airflow.datos.coderhivex.com",
    "recordType": "A",
    "ipAddress": "203.0.113.10",
    "proxied": true,
    "ttl": 1,
    "status": "Active",
    "daysSinceUpdate": 97,
    "createdAt": "2026-05-07T10:11:00.000Z",
    "updatedAt": "2026-05-07T10:11:00.000Z",
    "deletedAt": null
  }
]
```

| Parámetro | Ejemplo | Para qué |
|---|---|---|
| `cell` | `?cell=datos` | Qué tiene levantado un equipo |
| `userId` | `?userId=7` | Todo lo de una persona |
| `status` | `?status=Revoked` | Histórico — sin este filtro solo se ven los vivos |
| `minDaysSinceUpdate` | `?minDaysSinceUpdate=90` | Candidatos a revocar por inactividad |

Estados: `Provisioning`, `Active`, `Failed`, `Deleted`, `Revoked`. Un `status`
desconocido devuelve `400` en vez de una lista vacía, que sería indistinguible de
"no hay registros en ese estado".

### 12.2 Detalle sin filtro de propiedad

```http
GET /admin/dns/{id}
```

Incluye los estados terminales (`Deleted`/`Revoked`) — auditar es poder mirar lo
que ya no está vivo.

### 12.3 Revocar

```http
POST /admin/dns/{id}/revoke
Content-Type: application/json

{ "reason": "Inactivo por más de 90 días; se avisó al equipo el 2026-08-01." }
```

Elimina el registro en Cloudflare y lo marca `Revoked` en el catálogo con quién,
cuándo y por qué. El motivo es **obligatorio** (mínimo 10 caracteres).

Es `POST` y no `DELETE` porque lleva cuerpo obligatorio, y un `DELETE` con cuerpo
es algo que muchos proxies descartan en silencio.

`Revoked` es un estado distinto de `Deleted` a propósito: los dos liberan el
nombre, pero solo así una auditoría puede distinguir lo que el usuario dio de
baja de lo que el equipo le quitó.

**La plataforma no avisa al dueño.** El `userEmail` del listado está para que el
equipo lo contacte antes: un servicio que deja de resolver sin aviso es
indistinguible de una caída.
