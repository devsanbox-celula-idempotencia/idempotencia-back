---
id: dns-subdominios
title: Subdominios DNS (autoservicio)
sidebar_position: 8
sidebar_label: Subdominios DNS
---

# Subdominios DNS

Cualquier usuario de la plataforma puede crear, desde el panel, un subdominio
propio bajo `coderhivex.com` que apunte a su servicio o aplicación. No hace falta
pedirle nada al equipo de infraestructura: el backend crea el registro
automáticamente contra la API de Cloudflare.

## Estructura del nombre

```
[nombre-elegido-por-el-usuario].[celula].coderhivex.com
```

El **nombre** lo define el usuario al crear el registro; no es un valor fijo del
sistema. La **célula** es el equipo de trabajo: hoy el despliegue tiene una sola
(`idempotencia`), configurada en `Dns:DefaultCell`, así que el frontend **no
necesita mandarla**. El campo existe igual en el request para el día que haya
varias.

Por ejemplo, alguien que despliega su propia instancia de Airflow en la célula
`idempotencia` pide:

```
airflow.idempotencia.coderhivex.com
```

## Qué se crea exactamente

| | |
|---|---|
| Tipo de registro | **A** |
| Destino | La **IPv4 pública** del servidor del usuario |
| Proxy de Cloudflare | **Activado** (no configurable) |
| TTL | Automático |
| HTTPS | Automático, vía Total TLS de Cloudflare |

---

## Parte 1 — Flujo del usuario

### 1.1 Consultar la zona

Antes de mostrar el formulario, el frontend pide el dominio y el patrón para
armar la vista previa del nombre completo:

```http
GET /dns/zone
Authorization: Bearer <jwt>
```

```json
{
  "zoneName": "coderhivex.com",
  "defaultCell": "idempotencia",
  "pattern": "{label}.idempotencia.coderhivex.com"
}
```

El `pattern` viene ya resuelto con la célula por defecto: para la vista previa
alcanza con reemplazar `{label}` por lo que el usuario está escribiendo.

:::tip
No hardcodees el dominio en el frontend. Cambia entre ambientes (local, QA,
producción) y quedaría desincronizado sin que nadie se entere.
:::

### 1.2 Crear el subdominio

```http
POST /dns
Authorization: Bearer <jwt>
Content-Type: application/json

{
  "label": "airflow",
  "ipAddress": "203.0.113.10"
}
```

| Campo | Reglas |
|---|---|
| `label` | **Obligatorio.** 3–63 caracteres, solo `a-z`, `0-9` y `-`. No puede empezar ni terminar con guion, ni contener puntos. Se normaliza a minúsculas. |
| `cell` | **Opcional.** Si se omite se usa `idempotencia` (`Dns:DefaultCell`). Mismas reglas de formato que `label`. |
| `ipAddress` | **Obligatorio.** IPv4 **pública**. Ver la advertencia de abajo. |

Respuesta `201 Created`:

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

:::warning La IP tiene que ser alcanzable desde internet
Se rechazan los rangos privados (`10.x`, `172.16–31.x`, `192.168.x`), loopback
(`127.x`), link-local (`169.254.x`) y **CGNAT (`100.64.x`–`100.127.x`, el rango
típico de Tailscale)**.

No es una restricción caprichosa: el registro va proxeado, así que quien se
conecta al origen es el borde de Cloudflare **desde internet**, no el navegador
del usuario. Una IP privada es una IPv4 perfectamente válida y perfectamente
inalcanzable desde ahí: el subdominio se crearía sin error y fallaría después,
en el navegador de quien lo visite, con un error 522 que no explica nada.
:::

### 1.3 Errores posibles

| Código | Cuándo | Qué debería hacer la UI |
|---|---|---|
| `400` | Nombre o célula con formato inválido | Mostrar la regla concreta |
| `400` | Nombre o célula reservados (`www`, `api`, `mail`, `admin`, `db`…) | Sugerir probar otro |
| `400` | IP no pública | Explicar que debe ser la IP pública del servidor |
| `400` | Cuota alcanzada (**3 subdominios por usuario**) | Ofrecer el listado para eliminar uno |
| `409` | Ese subdominio ya está en uso | Sugerir otro nombre |
| `429` | Más de 10 escrituras por minuto | Pedir que reintente en un momento |
| `502` | El proveedor de DNS respondió con error | Reintentar |

Los `400` de nombre y el `409` son **corregibles por el usuario**: conviene que
la UI le sugiera probar otro nombre en vez de mostrar un error genérico.

### 1.4 Listar, ver y reapuntar

```http
GET    /dns            # mis subdominios
GET    /dns/{id}       # detalle
PUT    /dns/{id}       # reapuntar a otra IP
DELETE /dns/{id}       # eliminar
```

Para reapuntar (por ejemplo, tras migrar de servidor):

```http
PUT /dns/12
Authorization: Bearer <jwt>
Content-Type: application/json

{ "ipAddress": "203.0.113.99" }
```

**El nombre no se puede cambiar.** Eso sería otro subdominio: se elimina y se
crea uno nuevo (y así el catálogo conserva la historia de los dos).

Eliminar devuelve `204` y **libera el nombre**: la misma combinación
`label` + `cell` se puede volver a pedir después, incluso por otra persona. A
diferencia de las bases de datos, no hay que desactivar primero — no se destruye
ningún dato y la operación es reversible volviendo a crear el subdominio.

### 1.5 Comprobar que funciona

```bash
dig +short airflow.idempotencia.coderhivex.com
```

Como el registro está proxeado, esto devuelve **IPs de Cloudflare, no la del
usuario**. Es correcto: el proxy es lo que da el HTTPS automático y oculta el
origen.

```bash
curl -sI https://airflow.idempotencia.coderhivex.com | head -1
```

La propagación no es instantánea, pero con TTL automático suele estar lista en
segundos. La UI no debería prometer disponibilidad inmediata.

---

## Parte 2 — HTTPS: cómo funciona y qué hay que tener contratado

Esta sección es para el equipo de infraestructura. Es el punto que hay que
resolver **una vez** para que todo lo demás funcione.

### El problema de los dos niveles

El certificado comodín gratuito de Cloudflare (Universal SSL) cubre:

```
coderhivex.com
*.coderhivex.com          ← un solo nivel
```

Pero los subdominios de autoservicio tienen **dos**:

```
airflow.idempotencia.coderhivex.com
        ↑ este nivel extra queda fuera del comodín
```

Un comodín DNS cubre exactamente un nivel de profundidad. Sin nada más, el
subdominio resolvería bien y el navegador mostraría un error de certificado.

### La solución: Total TLS

**Total TLS** (parte de **Advanced Certificate Manager**, ~10 USD/mes) emite
certificados individuales para cada hostname **proxeado** que el comodín no
cubre. Se activa una vez sobre la zona:

```bash
curl -X PATCH "https://api.cloudflare.com/client/v4/zones/<ZONE_ID>/acm/total_tls" \
  -H "Authorization: Bearer <TOKEN_CON_SSL_EDIT>" \
  -H "Content-Type: application/json" \
  --data '{"enabled":true,"certificate_authority":"google"}'
```

:::info Por qué esto se hace a mano y no desde el backend
El token de `Dns:ApiToken` solo necesita `Zone.DNS: Edit` para la operación
normal. Este `PATCH` requiere además `Zone.SSL and Certificates: Edit`. No vale
la pena que el token de runtime —el que se usa en cada request de cada usuario—
cargue permanentemente con un permiso que se usaría una única vez.
:::

### De ahí sale que `proxied` no sea configurable

Total TLS solo actúa sobre hostnames proxeados. Por eso ni la creación ni la
actualización exponen ese campo: sería darle al usuario un botón para romper su
propio HTTPS sin entender por qué.

**Consecuencia:** al pasar por el proxy, el subdominio **solo enruta HTTP y
HTTPS**. No sirve para exponer un puerto TCP arbitrario (una base de datos, SSH).

:::danger Trampa conocida de Total TLS
Si alguien borra manualmente un certificado de Total TLS desde el panel,
Cloudflare interpreta que ese hostname se quiere **excluir** y no vuelve a
emitirlo, aunque el registro DNS se recree.

Si un subdominio queda sin HTTPS y todo lo demás parece correcto, es lo primero
a revisar.
:::

---

## Parte 3 — Administración y revocación

Endpoints bajo `/admin/dns`. Requieren **rol `Admin`** en el JWT: token
inválido o expirado → `401`, usuario sin el rol → `403`.

### 3.1 Auditar el inventario

```http
GET /admin/dns
Authorization: Bearer <jwt-admin>
```

Sin filtros devuelve todos los subdominios **vivos** de todos los usuarios,
ordenados del más antiguo al más reciente (lo más viejo primero: es lo que se
audita). Cada fila incluye el dueño y su correo:

```json
[
  {
    "dnsRecordId": 12,
    "userId": 7,
    "userEmail": "ana@coderhivex.com",
    "label": "airflow",
    "cell": "idempotencia",
    "fqdn": "airflow.idempotencia.coderhivex.com",
    "ipAddress": "203.0.113.10",
    "status": "Active",
    "daysSinceUpdate": 97,
    "createdAt": "2026-05-07T10:11:00.000Z",
    "updatedAt": "2026-05-07T10:11:00.000Z"
  }
]
```

Filtros disponibles (todos opcionales y combinables):

| Parámetro | Ejemplo | Para qué |
|---|---|---|
| `cell` | `?cell=idempotencia` | Qué tiene levantado un equipo |
| `userId` | `?userId=7` | Todo lo de una persona |
| `status` | `?status=Revoked` | Histórico (sin este filtro solo se ven los vivos) |
| `minDaysSinceUpdate` | `?minDaysSinceUpdate=90` | **Candidatos a revocar por inactividad** |

Estados posibles: `Provisioning`, `Active`, `Failed`, `Deleted`, `Revoked`.

:::note `Deleted` vs `Revoked`
Los dos liberan el nombre, pero significan cosas distintas: `Deleted` es el
usuario dando de baja lo suyo, `Revoked` es el equipo quitándoselo. La distinción
existe justamente para poder responder "¿qué revocamos este trimestre y por qué?"
sin cruzar tablas de logs.
:::

### 3.2 Procedimiento de revocación

**Paso 1 — Identificar candidatos.** Para inactividad:

```http
GET /admin/dns?minDaysSinceUpdate=90
Authorization: Bearer <jwt-admin>
```

Para abuso, buscar por célula o por usuario reportado.

**Paso 2 — Verificar antes de actuar.** Revocar tumba un servicio ajeno:

```http
GET /admin/dns/{id}
Authorization: Bearer <jwt-admin>
```

Este detalle incluye también los registros ya terminados — auditar es poder mirar
lo que ya no está vivo.

**Paso 3 — Avisar al dueño.** No lo hace la plataforma: el `userEmail` del
listado está justamente para que el equipo pueda contactarlo antes. Un servicio
que deja de resolver sin aviso es indistinguible de una caída.

**Paso 4 — Revocar.**

```http
POST /admin/dns/{id}/revoke
Authorization: Bearer <jwt-admin>
Content-Type: application/json

{ "reason": "Inactivo por más de 90 días; se avisó al equipo el 2026-08-01." }
```

El motivo es **obligatorio** (mínimo 10 caracteres) y queda guardado junto con
quién revocó y cuándo. Una auditoría que dice "revocado" sin decir por qué no
sirve para nada seis meses después: el costo de escribir esa línea lo paga el
operador una vez, el de no tenerla lo paga quien investigue.

:::info Por qué es `POST` y no `DELETE`
La revocación lleva un cuerpo obligatorio, y un `DELETE` con cuerpo es algo que
muchos proxies e intermediarios descartan en silencio.
:::

**Qué pasa internamente:** se elimina el registro en Cloudflare, se marca la fila
como `Revoked` con la constancia de la decisión, y el nombre queda libre. La
acción se registra en los logs del backend con nivel `Warning` — no
`Information`— para que sea fácil de encontrar sin filtrar entre el ruido del uso
normal.

### 3.3 Consultar el histórico de revocaciones

```http
GET /admin/dns?status=Revoked
Authorization: Bearer <jwt-admin>
```

---

## Límites y decisiones de diseño

| | |
|---|---|
| Cuota | **3 subdominios vivos por usuario**. Los eliminados y revocados no cuentan. Es la constante `@MaxRecordsPerUser` de `sp_ReserveDnsRecord`: cambiarla es un `CREATE OR ALTER` de ese SP, sin migración ni redespliegue del backend. |
| Rate limit | **10 escrituras por minuto y por usuario** (`POST`, `PUT`, `DELETE`). |
| Nombres reservados | `www`, `api`, `admin`, `mail`, `smtp`, `ns1`, `cdn`, `db`, `mysql`, `postgres`… Se validan **tanto en el nombre como en la célula**. |

El rate limit se particiona por usuario y no por IP porque el abuso relevante no
es que alguien se haga daño a sí mismo, sino que **agote la cuota de la API de
Cloudflare, que es compartida por toda la plataforma**.

:::caution Deuda conocida: la célula no valida pertenencia
Hoy **no se valida que el usuario pertenezca a la célula que declara**. No existe
todavía un catálogo de células ni una relación usuario↔célula en la base, así que
cualquier usuario autenticado puede crear un subdominio bajo el nombre de
cualquier célula.

Es una decisión consciente para no bloquear la entrega, pero no es el estado
final. Mientras siga así, **el control es a posteriori**: el listado por célula y
la revocación de esta misma sección.

Cuando exista el catálogo, el cambio es acotado: una tabla de células (más
membresía o una columna en `Users`) y una validación adicional dentro de
`sp_ReserveDnsRecord`. Nada del backend cambia — la célula ya viaja en el request
y ya se persiste en su propia columna.
:::
