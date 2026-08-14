# Guía para el frontend — Subdominios DNS

Todo lo que el frontend necesita para que un usuario pueda crear su propio
subdominio desde el panel: qué mandar, qué esperar de vuelta, qué validar antes
de llamar, qué errores manejar y cómo debería comportarse la UI.

Referencia completa de los endpoints en `docs/API.md` §11 y §12. El detalle de
infraestructura (certificados, revocación) está en
[Subdominios DNS](./dns-subdominios).

---

## 1. Qué se construye

El usuario elige un nombre y obtiene un subdominio que apunta a **su** servidor:

```
[nombre-que-elige].idempotencia.coderhivex.com
```

Por ejemplo, alguien que levantó Airflow en su VPS pide `airflow` y queda:

```
airflow.idempotencia.coderhivex.com   →   su IP pública
```

`idempotencia` es la célula (equipo de trabajo). **El frontend no la manda**: el
backend la pone sola. Hay un campo `cell` opcional en el request para el día que
haya varias células, pero hoy no hay que usarlo.

El HTTPS queda resuelto solo. El usuario no genera certificados ni configura
nada de TLS en su servidor.

---

## 2. Antes de escribir código

**Autenticación.** Todos los endpoints necesitan el JWT del login:

```
Authorization: Bearer <token>
```

Sin token o con token vencido → `401`. Es el mismo token del resto de la API.

**Lo único que el usuario tiene que saber es su IP pública.** No un dominio, no
un puerto, no un certificado: la IPv4 pública del servidor donde corre su
servicio. Vale la pena decírselo así de explícito en la UI, porque es el punto
donde más se traba la gente.

**El servicio escucha en el puerto 80/443 de esa IP.** El subdominio pasa por el
proxy de Cloudflare, que solo enruta HTTP y HTTPS. No se puede publicar un
puerto arbitrario (una base de datos, SSH) por esta vía.

---

## 3. Los endpoints de un vistazo

| Método | Ruta | Para qué |
|---|---|---|
| `GET` | `/dns/zone` | Dominio y patrón, para la vista previa |
| `POST` | `/dns` | Crear un subdominio |
| `GET` | `/dns` | Listar los míos |
| `GET` | `/dns/{id}` | Detalle de uno |
| `PUT` | `/dns/{id}` | Reapuntar a otra IP |
| `DELETE` | `/dns/{id}` | Eliminar |

**Cuota: 3 subdominios por usuario.** Los eliminados no cuentan.
**Rate limit: 10 escrituras por minuto** (`POST`, `PUT`, `DELETE`).

---

## 4. Tipos

```ts
// Lo que devuelve la API para un subdominio
interface DnsRecord {
  dnsRecordId: number;
  label: string;          // "airflow"
  cell: string;           // "idempotencia"
  fqdn: string;           // "airflow.idempotencia.coderhivex.com"
  recordType: 'A';
  ipAddress: string;      // "203.0.113.10"
  proxied: boolean;       // siempre true
  ttl: number;            // siempre 1 (automático)
  status: 'Provisioning' | 'Active' | 'Failed' | 'Deleted' | 'Revoked';
  createdAt: string;      // ISO 8601 UTC
  updatedAt: string;
  deletedAt: string | null;
}

interface DnsZone {
  zoneName: string;       // "coderhivex.com"
  defaultCell: string;    // "idempotencia"
  pattern: string;        // "{label}.idempotencia.coderhivex.com"
}

interface CreateDnsRecordRequest {
  label: string;
  ipAddress: string;
  cell?: string;          // omitir: el backend usa "idempotencia"
}

interface UpdateDnsRecordRequest {
  ipAddress: string;      // es lo único que se puede cambiar
}
```

En la práctica el frontend solo va a ver `status: 'Active'`. Los otros valores
existen para el catálogo interno y para la vista de administración; `Provisioning`
y `Failed` son estados transitorios que no llegan a una respuesta exitosa, y los
eliminados/revocados no aparecen en el listado del usuario.

---

## 5. Cliente

El manejo de errores está separado porque la API usa **dos formatos distintos**
según de dónde venga el error (§7):

```ts
const API = import.meta.env.VITE_API_URL;

class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public fieldErrors?: Record<string, string[]>,
    public retryAfter?: number,
  ) {
    super(message);
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${getToken()}`,
      ...options.headers,
    },
  });

  if (res.status === 204) return undefined as T;

  const body = await res.json().catch(() => null);

  if (!res.ok) {
    // Validación de modelo: ValidationProblemDetails, con errores por campo.
    if (body?.errors) {
      const first = Object.values(body.errors as Record<string, string[]>)[0]?.[0];
      throw new ApiError(res.status, first ?? 'Datos inválidos.', body.errors);
    }
    // Todo lo demás: { status, error }
    throw new ApiError(
      res.status,
      body?.error ?? 'Ocurrió un error inesperado.',
      undefined,
      Number(res.headers.get('Retry-After')) || undefined,
    );
  }

  return body as T;
}

export const dnsApi = {
  getZone: () => request<DnsZone>('/dns/zone'),

  list: () => request<DnsRecord[]>('/dns'),

  get: (id: number) => request<DnsRecord>(`/dns/${id}`),

  create: (data: CreateDnsRecordRequest) =>
    request<DnsRecord>('/dns', { method: 'POST', body: JSON.stringify(data) }),

  update: (id: number, ipAddress: string) =>
    request<DnsRecord>(`/dns/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ipAddress }),
    }),

  remove: (id: number) => request<void>(`/dns/${id}`, { method: 'DELETE' }),
};
```

---

## 6. Pantalla "crear subdominio"

### 6.1 La vista previa

Pedí `GET /dns/zone` una vez al montar la pantalla y armá el nombre completo en
vivo mientras el usuario escribe:

```tsx
const { data: zone } = useQuery({ queryKey: ['dns-zone'], queryFn: dnsApi.getZone });

const preview = label
  ? zone?.pattern.replace('{label}', label)
  : zone?.pattern;

// airflow  →  airflow.idempotencia.coderhivex.com
```

:::warning No hardcodees el dominio
Traelo siempre de `/dns/zone`. Cambia entre ambientes (local, QA, producción) y
un valor cableado en el frontend queda desincronizado sin que nadie se entere
hasta que alguien copia un nombre que no existe.
:::

### 6.2 Validar en el cliente

Estas dos reglas conviene validarlas antes de llamar, para dar feedback
inmediato:

```ts
// Mismo patrón que el backend: 3–63 caracteres, letras/números/guiones,
// sin guion al principio ni al final, sin puntos.
const LABEL_RE = /^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])$/;

function validarLabel(label: string): string | null {
  const v = label.trim().toLowerCase();
  if (!v) return 'Elegí un nombre para tu subdominio.';
  if (v.length < 3) return 'El nombre debe tener al menos 3 caracteres.';
  if (v.length > 63) return 'El nombre no puede superar los 63 caracteres.';
  if (v.includes('.')) return 'El nombre no puede contener puntos.';
  if (!LABEL_RE.test(v))
    return 'Solo letras, números y guiones. No puede empezar ni terminar con guion.';
  return null;
}

// Forma de la IP. El backend hace la validación seria (ver 6.3).
const IPV4_RE = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;

function validarIp(ip: string): string | null {
  const m = ip.trim().match(IPV4_RE);
  if (!m) return 'Escribí una dirección IPv4, por ejemplo 203.0.113.10';
  if (m.slice(1).some((o) => Number(o) > 255)) return 'Cada número debe estar entre 0 y 255.';
  return null;
}
```

**Normalizá el label a minúsculas mientras se escribe.** El backend lo hace igual,
pero si la UI muestra `Airflow` en la vista previa y el resultado llega como
`airflow`, parece un bug.

### 6.3 Lo que NO hay que validar en el cliente

- **Nombres reservados** (`www`, `api`, `mail`, `admin`, `db`…). La lista vive en
  la base y se puede ampliar sin redesplegar nada; duplicarla en el frontend
  garantiza que quede vieja. Mostrá el `400` del backend.
- **Si el nombre ya está tomado.** No hay endpoint de "chequear disponibilidad" a
  propósito: entre el chequeo y la creación otro usuario puede ganar la carrera,
  así que igual hay que manejar el `409`. Un chequeo previo solo agrega una
  llamada y una falsa sensación de certeza.
- **Si la IP es privada o pública.** Es una lista larga de rangos reservados y
  mantenerla en dos lados es pedir que se desincronicen. El `400` del backend
  trae un mensaje que se puede mostrar tal cual.

### 6.4 Crear

```ts
const record = await dnsApi.create({
  label: 'airflow',
  ipAddress: '203.0.113.10',
  // cell: se omite → "idempotencia"
});
```

Respuesta `201`:

```json
{
  "dnsRecordId": 12,
  "label": "airflow",
  "cell": "idempotencia",
  "fqdn": "airflow.idempotencia.coderhivex.com",
  "recordType": "A",
  "ipAddress": "203.0.113.10",
  "proxied": true,
  "ttl": 1,
  "status": "Active",
  "createdAt": "2026-08-12T15:04:11.120Z",
  "updatedAt": "2026-08-12T15:04:11.120Z"
}
```

Mostrale el `fqdn` en grande, con un botón de copiar y un enlace a
`https://{fqdn}`. Es lo único que el usuario se lleva de esta pantalla.

### 6.5 Después de crear: la espera

El registro existe de inmediato, pero **el nombre puede tardar unos segundos en
resolver** y el certificado en emitirse. Si el usuario hace clic en el enlace en
el mismo segundo, es posible que le dé error.

Decílo en la UI en vez de dejar que lo descubra:

> Tu subdominio está listo. Puede tardar hasta un minuto en estar disponible
> desde todos lados.

No hace falta hacer polling contra ningún endpoint: la API ya devolvió
`status: "Active"` y no va a cambiar. La demora es de propagación DNS, no del
backend.

---

## 7. Errores

La API usa **dos formatos**. El cliente de la §5 ya los normaliza, pero conviene
saber por qué:

```jsonc
// Validación de modelo (formato del label, IP mal escrita, campo faltante)
{ "status": 400, "errors": { "Label": ["..."], "IpAddress": ["..."] } }

// Todo lo demás (reglas de negocio, cuota, colisión, rate limit, 500)
{ "status": 409, "error": "El subdominio 'airflow.idempotencia.coderhivex.com' ya está en uso. Elige otra etiqueta." }
```

**Los mensajes del segundo formato están escritos para mostrarse tal cual.** No
hace falta traducirlos ni reemplazarlos.

| Código | Qué pasó | Qué debería hacer la UI |
|---|---|---|
| `400` | Formato del nombre o de la IP | Marcar el campo con el mensaje del backend |
| `400` | Nombre reservado por la plataforma | Marcar el campo y sugerir otro nombre |
| `400` | **La IP no es pública** | Ver el recuadro de abajo — merece una explicación, no solo el mensaje |
| `400` | Cuota de 5 alcanzada | Ofrecer ir al listado para eliminar uno |
| `409` | **El nombre ya está tomado** | Marcar el campo, mantener el foco, sugerir variantes |
| `401` | Token vencido | Renovar sesión / redirigir a login |
| `429` | Demasiadas operaciones | Deshabilitar el botón `Retry-After` segundos |
| `502` | El proveedor de DNS falló | "No pudimos crear el subdominio ahora. Intentá de nuevo." + botón de reintento |
| `404` | El subdominio no existe o no es tuyo | Refrescar el listado |

### El caso de la IP privada

Es el error que más se va a ver, y el mensaje solo no alcanza para que el usuario
entienda qué hacer:

> **Esa IP no funciona desde internet.**
> Necesitamos la IP **pública** de tu servidor, no la de tu red local.
> No sirven las que empiezan con `10.`, `192.168.`, `172.16`–`172.31`, `127.` ni
> `100.64`–`100.127` (Tailscale).
> Si no la sabés, corré esto en tu servidor: `curl ifconfig.me`

Ese último comando resuelve el 90% de los casos y ahorra un ticket de soporte.

### Manejo del `429`

```ts
catch (e) {
  if (e instanceof ApiError && e.status === 429) {
    setCooldown(e.retryAfter ?? 60);   // deshabilitar el botón ese tiempo
  }
}
```

---

## 8. Pantalla "mis subdominios"

```ts
const records = await dnsApi.list();
```

Devuelve solo los vivos, del más reciente al más viejo. Por cada uno mostrá:

- El **`fqdn`** como enlace a `https://{fqdn}`, con botón de copiar.
- La **`ipAddress`** a la que apunta.
- `createdAt` en formato relativo ("hace 3 días").
- Acciones: **Reapuntar** y **Eliminar**.

Mostrá también el uso de la cuota (`records.length` de 3) y deshabilitá el botón
de crear cuando llegue al tope, con el motivo visible. Es mejor que dejarlo
habilitado para que falle con un `400`.

Si el listado viene vacío, un estado vacío que explique para qué sirve esto vale
más que una tabla sin filas.

---

## 9. Reapuntar y eliminar

### Reapuntar

```ts
await dnsApi.update(12, '203.0.113.99');
```

Lo único que se puede cambiar es la IP. **El nombre no**: para eso hay que
eliminar y crear otro. Decílo en la UI —"para cambiar el nombre, eliminá este
subdominio y creá uno nuevo"— en vez de mostrar un campo deshabilitado sin
explicación.

Mismas validaciones de IP que al crear, mismos errores.

### Eliminar

```ts
await dnsApi.remove(12);   // 204 No Content
```

Pedí confirmación **escribiendo el nombre completo**, no un "¿estás seguro?".
Eliminar tumba un servicio que puede estar en uso:

> Vas a eliminar `airflow.idempotencia.coderhivex.com`. El sitio va a dejar de
> responder inmediatamente.
> Escribí `airflow` para confirmar.

Después de eliminar, el nombre **queda libre** y cualquiera lo puede volver a
pedir — incluido el mismo usuario. No hay paso previo de desactivación (a
diferencia de las bases de datos): no se pierden datos, solo el nombre deja de
resolver.

---

## 10. Preguntas que van a llegar a soporte

**"`dig` me devuelve una IP que no es la mía."**
Correcto. El subdominio pasa por el proxy de Cloudflare, así que resuelve a IPs
de Cloudflare y el tráfico se reenvía a la del usuario. Es lo que da el HTTPS
automático y de paso oculta el origen.

**"¿Puedo apuntar a `mi-app.railway.app` en vez de a una IP?"**
Hoy no: solo registros A con IPv4. Un CNAME necesita otra validación de destino
(apuntar a infraestructura ajena tiene sus propios riesgos) y no está
implementado.

**"¿Puedo publicar mi Postgres en `db.idempotencia.coderhivex.com`?"**
No. El proxy solo enruta HTTP y HTTPS. Para bases de datos está
`POST /databases`, que entrega host y puerto directos.

**"El sitio da error de certificado."**
Si el subdominio resuelve pero el HTTPS falla, no es del frontend ni del usuario:
es la configuración de certificados de la zona. Escalar a infraestructura con el
FQDN exacto (ver [Subdominios DNS](./dns-subdominios), sección de HTTPS).

**"Creé el subdominio pero no carga nada."**
Que verifique que su servicio responde en el puerto 80 o 443 de esa IP, desde
afuera de su red. Un servicio escuchando solo en `localhost` o detrás de un
firewall no es alcanzable para Cloudflare.

---

## 11. Checklist para la UI

- [ ] El dominio sale de `GET /dns/zone`, no está hardcodeado.
- [ ] Vista previa del nombre completo en vivo mientras se escribe.
- [ ] El label se normaliza a minúsculas en el input.
- [ ] Validación local de formato de label e IP antes de llamar.
- [ ] **No** se duplican en el frontend: nombres reservados, rangos de IP
      privadas, ni chequeo de disponibilidad.
- [ ] El `409` deja el foco en el campo del nombre y sugiere variantes.
- [ ] El `400` de IP privada muestra la explicación completa con `curl ifconfig.me`.
- [ ] El `429` deshabilita el botón durante `Retry-After` segundos.
- [ ] Se avisa que la propagación puede tardar hasta un minuto.
- [ ] El `fqdn` se muestra con botón de copiar y enlace.
- [ ] La cuota (5) se muestra y el botón de crear se deshabilita al llegar al tope.
- [ ] Eliminar pide escribir el nombre para confirmar.
- [ ] Se explica que el nombre no se puede editar, solo la IP.

---

## 12. Si tu usuario tiene rol `Admin`

Hay tres endpoints más, bajo `/admin/dns`, que devuelven `403` para el resto:

| Método | Ruta | Para qué |
|---|---|---|
| `GET` | `/admin/dns` | Inventario de todos los usuarios |
| `GET` | `/admin/dns/{id}` | Detalle sin filtro de propiedad |
| `POST` | `/admin/dns/{id}/revoke` | Revocar (motivo obligatorio) |

Filtros del listado: `?cell=`, `?userId=`, `?status=`, `?minDaysSinceUpdate=`.
Cada fila agrega `userId`, `userEmail` y `daysSinceUpdate`.

Para una pantalla de administración, `?minDaysSinceUpdate=90` es la vista más
útil: son los candidatos a revocar por inactividad. El contrato completo y el
procedimiento están en `docs/API.md` §12 y en
[Subdominios DNS](./dns-subdominios).
