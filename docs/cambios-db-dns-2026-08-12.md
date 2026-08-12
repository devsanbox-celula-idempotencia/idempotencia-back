# Cambios en la base de datos — autoservicio de subdominios DNS

**Fecha:** 2026-08-12
**Script ejecutable:** [`sql/2026-08-12-dns-records.sql`](../sql/2026-08-12-dns-records.sql)
**Instancia:** el catálogo en SQL Server (la misma donde viven `Users` y
`ProvisionedDatabases`). En Cloudflare no hay nada que preparar por SQL.

> **Este documento reemplaza a una versión anterior del mismo día**, que asumía
> la estructura `{label}.idempotencia.<zona>` y que el subdominio acompañaba a
> una base de datos. El servicio real es otro: **autoservicio de subdominios
> para los proyectos de los usuarios**, bajo `{label}.idempotencia.coderhivex.com`.
> Si el script anterior **no** se ejecutó, ignoralo. Si sí, ver §10.

---

## 1. Qué hace el servicio

Un usuario pide desde el panel un subdominio para su proyecto:

```
[nombre-elegido].idempotencia.coderhivex.com     ej: airflow.idempotencia.coderhivex.com
```

- El **nombre** lo elige el usuario (`airflow`), no es un valor fijo del sistema.
- La **célula** es su equipo de trabajo. Hoy hay una sola (`idempotencia`), que
  el backend aporta por defecto desde `Dns:DefaultCell`; el SP la recibe siempre
  explícita, así que la base ya soporta varias sin cambios.
- Se crea un registro **A** hacia la **IPv4 pública** que aporta el usuario.
- El registro va **proxeado** por Cloudflare, y de ahí sale el HTTPS (§7).

---

## 2. Resumen de objetos

| Objeto | Tipo | Acción |
|---|---|---|
| `dbo.DnsRecords` | Tabla | **Nueva** |
| `dbo.DnsReservedLabels` | Tabla | **Nueva** (+ semilla de 27 etiquetas) |
| `UX_DnsRecords_Fqdn_Alive` | Índice único filtrado | **Nuevo** |
| `IX_DnsRecords_UserId` | Índice | **Nuevo** |
| `IX_DnsRecords_Cell` | Índice | **Nuevo** |
| `IX_DnsRecords_Fqdn` | Índice | **Nuevo** |
| `FK_DnsRecords_Users` | Clave foránea | **Nueva** (condicional — §6) |
| `sp_ReserveDnsRecord` | SP | **Nuevo** |
| `sp_ConfirmDnsRecord` | SP | **Nuevo** |
| `sp_FailDnsRecord` | SP | **Nuevo** |
| `sp_GetUserDnsRecords` | SP | **Nuevo** |
| `sp_GetDnsRecordDetail` | SP | **Nuevo** |
| `sp_UpdateDnsRecord` | SP | **Nuevo** |
| `sp_DeleteDnsRecord` | SP | **Nuevo** |
| `sp_GetAllDnsRecords` | SP | **Nuevo** (admin) |
| `sp_GetDnsRecordDetailAdmin` | SP | **Nuevo** (admin) |
| `sp_RevokeDnsRecord` | SP | **Nuevo** (admin) |

**No se modifica ninguna tabla ni SP existente.** Todo es aditivo: el script se
puede aplicar sin ventana de mantenimiento y sin afectar lo que ya está en
producción. Es idempotente (`IF OBJECT_ID(...) IS NULL`, `CREATE OR ALTER`,
`MERGE`).

---

## 3. Tabla `DnsRecords`

```
DnsRecordId       INT IDENTITY   PK
UserId            INT            NOT NULL   -- dueño
Label             NVARCHAR(63)   NOT NULL   -- "airflow"
Cell              NVARCHAR(63)   NOT NULL   -- "datos"
Fqdn              NVARCHAR(255)  NOT NULL   -- airflow.idempotencia.coderhivex.com
RecordType        NVARCHAR(10)   NOT NULL   -- A (hoy el único que crea el autoservicio)
Content           NVARCHAR(255)  NOT NULL   -- IPv4 pública del servicio del usuario
Proxied           BIT            NOT NULL   DEFAULT 1
Ttl               INT            NOT NULL   DEFAULT 1        -- 1 = automático
ProviderRecordId  NVARCHAR(64)   NULL       -- id del registro en Cloudflare
Status            NVARCHAR(20)   NOT NULL   DEFAULT 'Provisioning'
CreatedAt         DATETIME2(3)   NOT NULL
UpdatedAt         DATETIME2(3)   NOT NULL
DeletedAt         DATETIME2(3)   NULL
RevokedByUserId   INT            NULL       -- \
RevokedAt         DATETIME2(3)   NULL       --  } auditoría de revocación
RevokeReason      NVARCHAR(500)  NULL       -- /
```

### Decisiones que conviene entender

**Cinco estados, con dos terminales distintos.**
`Provisioning → Active | Failed`, y luego `Deleted` o `Revoked`. La diferencia
entre los dos últimos es **quién actuó**: `Deleted` es el usuario dando de baja
lo suyo, `Revoked` es el equipo quitándoselo. Podrían colapsarse en uno, pero
entonces una auditoría no podría responder "¿cuántos subdominios revocamos este
mes y por qué?" sin cruzar tablas de logs que no existen. Los dos liberan el
nombre por igual.

**`ProviderRecordId` es NULLABLE.** Al reservar todavía no existe el registro en
Cloudflare, así que no hay id que guardar; lo escribe `sp_ConfirmDnsRecord`. Que
sea nullable es lo que hace posible el patrón reservar → crear → confirmar sin
transacciones distribuidas.

**El FQDN se guarda compuesto.** El backend le pasa `Dns:ZoneName` al SP, el SP
arma `{label}.{cell}.{zona}` y lo persiste. A partir de ahí el catálogo es la
única fuente del nombre real: si el backend lo recompusiera en cada operación,
cambiar la configuración dejaría los registros ya creados apuntando a un nombre
distinto del que quedó en Cloudflare, y el borrado no encontraría nada.

**Constraints:**

- `CK_DnsRecords_Status` → los cinco estados. Deben coincidir con la clase
  `DnsRecordStatus` del backend; si se agrega uno de un lado y no del otro, el SP
  falla con un 547 — **es exactamente el bug 24** del catálogo de bases de datos.
- `CK_DnsRecords_Type` → `A | AAAA | CNAME | TXT`. Hoy el autoservicio solo crea
  `A`; los otros están permitidos en la tabla, pero habilitarlos no es solo
  relajar una validación (cada tipo necesita su propia forma de validar el
  destino).
- `CK_DnsRecords_Ttl` → `Ttl = 1 OR (Ttl BETWEEN 60 AND 86400)`. El `1`
  ("automático") queda fuera del rango válido, por eso es una disyunción.
- `CK_DnsRecords_Revoked` → o están las tres columnas de auditoría, o ninguna.
  Evita el registro "revocado por nadie, sin motivo" que aparece cuando alguien
  hace un `UPDATE` a mano.

---

## 4. Índices — dónde está la garantía de no-colisión

### `UX_DnsRecords_Fqdn_Alive` (único, filtrado)

```sql
CREATE UNIQUE NONCLUSTERED INDEX UX_DnsRecords_Fqdn_Alive
    ON dbo.DnsRecords (Fqdn)
    WHERE Status IN ('Provisioning', 'Active');
```

Es **la** implementación del requisito "evitar colisiones de nombres, validar que
el subdominio no esté en uso".

Es filtrado y no un `UNIQUE` plano porque un subdominio eliminado o revocado debe
liberar el nombre, mientras la fila se conserva para auditoría. Con un `UNIQUE`
plano, el primer usuario que creara y borrara `airflow.datos` lo bloquearía para
siempre.

> **Sobre carreras.** `sp_ReserveDnsRecord` comprueba la colisión antes de
> insertar, pero eso es cortesía: sirve para devolver un mensaje entendible en
> lugar de un error 2601 que el middleware traduciría a un 500 genérico. Con dos
> peticiones simultáneas pidiendo el mismo nombre, quién gana lo decide el
> índice. Es el orden correcto — el índice es la garantía dura.

### Los otros tres

- `IX_DnsRecords_UserId` `(UserId, Status)` + `INCLUDE`: cubre el listado del
  usuario y el conteo de cuota.
- `IX_DnsRecords_Cell` `(Cell, Status)`: cubre la consulta natural de auditoría
  ("qué tiene levantado el equipo X").
- `IX_DnsRecords_Fqdn`: la reconciliación de huérfanos busca por FQDN **sin**
  filtrar por estado, así que no puede usar el índice filtrado.

---

## 5. Cuota y etiquetas reservadas

**Cuota: 3 subdominios vivos por usuario**, constante `@MaxRecordsPerUser` en
`sp_ReserveDnsRecord`. Cuenta solo `Provisioning` y `Active`, así que lo
eliminado o revocado no sigue consumiendo cupo. Si mañana hay planes distintos,
esa constante se convierte en una lectura de `Users` y el resto del SP no cambia.

**`DnsReservedLabels`** se valida contra el **label Y contra la célula**: una
célula llamada `www` o `mail` comprometería el dominio igual que una etiqueta con
ese nombre. Es una tabla y no una lista en el SP para que agregar una prohibición
sea un `INSERT` y no un redespliegue:

```sql
INSERT INTO dbo.DnsReservedLabels (Label, Reason)
VALUES ('soporte', 'Reservada: mesa de ayuda.');
```

---

## 6. ⚠️ Dependencias sobre el esquema existente

Hay **dos**, y las dos están aisladas para que el script no se rompa:

**a) La clave foránea** asume `dbo.Users(UserId)`. Si no la encuentra, avisa con
un `PRINT` y sigue. Verificar después de correr:

```sql
SELECT name FROM sys.foreign_keys WHERE name = 'FK_DnsRecords_Users';
```

Si no devuelve nada:

```sql
ALTER TABLE dbo.DnsRecords
    ADD CONSTRAINT FK_DnsRecords_Users
        FOREIGN KEY (UserId) REFERENCES dbo.<TablaReal> (<ColumnaReal>);
```

**b) El correo del dueño en el listado administrativo.** `sp_GetAllDnsRecords`
hace `LEFT JOIN` con `dbo.Users` para traer `Email`. El script detecta si esa
columna existe y **genera una de dos variantes del SP**: con el JOIN, o
devolviendo `UserEmail` vacío y un `PRINT` de aviso. Así el despliegue no se cae
por un nombre de columna distinto.

**Sin `ON DELETE CASCADE`, a propósito:** borrar un usuario no debe hacer
desaparecer en silencio filas cuyos registros siguen existiendo en Cloudflare. El
orden correcto es eliminar sus subdominios por la API y después el usuario.

---

## 7. HTTPS — por qué `Proxied` no es negociable

Esto no es un cambio de base de datos, pero explica el `DEFAULT (1)` de la
columna `Proxied` y **es lo que hay que resolver en Cloudflare** para que el
requisito de SSL se cumpla.

El certificado comodín gratuito de Cloudflare (Universal SSL) cubre
`*.coderhivex.com` — **un solo nivel**. `airflow.idempotencia.coderhivex.com` tiene
**dos**, así que ese comodín no lo cubre y el navegador daría un error de
certificado.

Quien resuelve eso es **Total TLS**, parte de **Advanced Certificate Manager**:
emite certificados individuales para cada hostname **proxeado** que no esté
cubierto por el comodín. De ahí las dos consecuencias:

1. **Hay que contratar ACM y activar Total TLS en la zona** (~10 USD/mes). Sin
   eso, los subdominios resuelven pero no tienen HTTPS válido.
2. **Los registros van proxeados sí o sí.** Total TLS solo actúa sobre hostnames
   proxeados. Por eso ni `CreateDnsRecordRequest` ni `UpdateDnsRecordRequest`
   exponen `proxied`: sería darle al usuario un botón para romper su propio
   HTTPS sin entender por qué.

Efecto colateral a tener presente: al pasar por el proxy, el subdominio **solo
enruta HTTP y HTTPS**. No sirve para exponer un puerto TCP arbitrario.

Activar Total TLS (una vez, sobre la zona):

```bash
curl -X PATCH "https://api.cloudflare.com/client/v4/zones/<ZONE_ID>/acm/total_tls" \
  -H "Authorization: Bearer <TOKEN_CON_PERMISO_SSL_EDIT>" \
  -H "Content-Type: application/json" \
  --data '{"enabled":true,"certificate_authority":"google"}'
```

> El token de `Dns:ApiToken` solo necesita `Zone.DNS: Edit` para la operación
> normal. Este `PATCH` requiere además `Zone.SSL and Certificates: Edit`, y por
> eso se hace **a mano una sola vez** en vez de desde el backend: no vale la pena
> que el token de runtime cargue con un permiso que usaría una única vez.

> ⚠️ **Trampa documentada de Total TLS:** si alguien borra manualmente un
> certificado de Total TLS, Cloudflare interpreta que ese hostname se quiere
> excluir y **no vuelve a emitirlo**, aunque el registro DNS se recree. Si un
> subdominio queda sin HTTPS y todo lo demás está bien, es lo primero a mirar.

---

## 8. Los 10 stored procedures

| SP | Lo invoca | Qué hace |
|---|---|---|
| `sp_ReserveDnsRecord` | `POST /dns` | Valida formato de label y célula, reservadas, formato del destino, cuota y colisión; inserta en `Provisioning`; devuelve el FQDN armado. |
| `sp_ConfirmDnsRecord` | `POST /dns` (paso 3) | Pasa a `Active` y guarda el `ProviderRecordId`. |
| `sp_FailDnsRecord` | reversión de `POST /dns` | Pasa a `Failed`. No lanza nunca. |
| `sp_GetUserDnsRecords` | `GET /dns` | Subdominios vivos del usuario. Sin `ProviderRecordId`. |
| `sp_GetDnsRecordDetail` | `GET /dns/{id}` + interno | Detalle **con** `ProviderRecordId`, filtrado por dueño. |
| `sp_UpdateDnsRecord` | `PUT /dns/{id}` | Persiste la IP nueva. **No recibe `Proxied` ni `Ttl`.** |
| `sp_DeleteDnsRecord` | `DELETE /dns/{id}` | Baja del usuario → `Deleted`. |
| `sp_GetAllDnsRecords` | `GET /admin/dns` | Inventario con filtros: célula, dueño, estado, días sin modificar. |
| `sp_GetDnsRecordDetailAdmin` | `GET /admin/dns/{id}` | Detalle sin filtro de propiedad, incluye terminales. |
| `sp_RevokeDnsRecord` | `POST /admin/dns/{id}/revoke` | → `Revoked` + quién, cuándo y por qué. |

### Dos detalles de diseño en los SPs de administración

**`sp_GetDnsRecordDetailAdmin` es un SP aparte y no un `@UserId = NULL` del
otro.** Un parámetro que significa "no filtres" convierte un olvido en una fuga
de datos de todos los usuarios. Con dos SPs, el que no filtra solo se puede
invocar desde el repositorio administrativo.

**Los filtros de `sp_GetAllDnsRecords` usan `@Param IS NULL OR columna =
@Param`**, no SQL dinámico: una sola consulta para cualquier combinación, sin
abrir una superficie de inyección donde hoy no la hay. `@MinDaysSinceUpdate`
calcula la antigüedad **en la base** para que el filtro y el valor que se muestra
usen el mismo reloj — es el insumo de la revocación por inactividad.

### Códigos de error (`THROW`)

Rango 50030–50044; el middleware ya los traduce a **400** con el mensaje tal cual
(`ApiExceptionMapper`: `SqlException` con `Number >= 50000` → 400). Los rangos ya
usados por el catálogo de bases de datos (50011, 50020) no se tocan.

| Código | Mensaje |
|---|---|
| 50030 | No se recibió el dominio de zona. |
| 50031 | El nombre debe tener entre 3 y 63 caracteres. |
| 50032 | Nombre: solo letras, números y guiones, sin guion al inicio/fin. |
| 50033 | La célula debe tener entre 3 y 63 caracteres. |
| 50034 | Célula: solo letras, números y guiones, sin guion al inicio/fin. |
| 50035 | Nombre o célula reservados por la plataforma. |
| 50036 | El destino de un registro A debe ser una IPv4. |
| 50037 | Alcanzaste el máximo de subdominios. |
| 50038 | El FQDN supera los 255 caracteres. |
| 50039 | Ese subdominio ya está en uso. |
| 50040 | La reserva no existe o no está pendiente de confirmar. |
| 50041 | No existe, no te pertenece, o no está activo. |
| 50042 | No existe, no te pertenece, o ya fue eliminado. |
| 50043 | La revocación requiere un motivo. |
| 50044 | El subdominio no existe o ya no está activo. |

---

## 9. Contrato con EF Core — cuidado al editar

El backend mapea los resultados sobre tipos *keyless*. **Los nombres de las
columnas del `SELECT` final son el contrato**: si se renombra una, EF no falla,
deja la propiedad en su valor por defecto **en silencio**.

| SP | Tipo del backend | Columnas |
|---|---|---|
| `sp_ReserveDnsRecord` | `DnsRecordReservation` | `DnsRecordId, Label, Cell, Fqdn, RecordType, Content, Proxied, Ttl, CreatedAt` |
| `sp_GetUserDnsRecords` | `DnsRecordInfo` | `DnsRecordId, UserId, Label, Cell, Fqdn, RecordType, Content, Proxied, Ttl, Status, CreatedAt, UpdatedAt, DeletedAt` |
| `sp_GetDnsRecordDetail` / `…Admin` | `DnsRecordDetail` | igual que el anterior **+ `ProviderRecordId`** |
| `sp_GetAllDnsRecords` | `DnsRecordAdminInfo` | el de `Info` **+ `UserEmail` + `DaysSinceUpdate`** |

`DnsRecordAdminInfo` es un tipo aparte y no una subclase de `DnsRecordInfo` por
esto mismo: dos SPs con columnas distintas = dos tipos.

---

## 10. Si ya se ejecutó la versión anterior del script

La tabla de la v1 no tiene `Cell` ni las columnas de revocación, y este script no
altera una tabla que ya existe. Como en la v1 no había registros reales:

```sql
SELECT COUNT(*) FROM dbo.DnsRecords WHERE Status IN ('Provisioning','Active');
-- Si devuelve 0:
DROP TABLE dbo.DnsRecords;
-- y volver a ejecutar el script completo.
```

Si devuelve algo distinto de 0, esos registros **existen en Cloudflare**: hay que
borrarlos por el panel o la API antes de tirar la tabla, o quedan resolviendo sin
nadie que los administre.

---

## 11. Orden de despliegue

1. **Cloudflare:** contratar ACM y activar Total TLS en la zona `coderhivex.com`
   (§7). Sin esto los subdominios resuelven sin HTTPS válido.
2. Ejecutar `sql/2026-08-12-dns-records.sql` en el catálogo.
3. Verificar la FK (§6a), el aviso de `Email` (§6b) y los 10 SPs:
   ```sql
   SELECT name FROM sys.procedures WHERE name LIKE 'sp_%Dns%' ORDER BY name;
   ```
4. Configurar la sección `Dns` (en despliegue, el token por entorno):
   ```
   Dns__ZoneId=c1c62663d28fa916dc9bc030103e6e83
   Dns__ApiToken=cfut_…
   Dns__ZoneName=coderhivex.com
   ```
   `Program.cs` valida `ZoneId`, `ApiToken` y `ZoneName` al arrancar y **no
   levanta** si falta alguna, igual que con `Provisioning:IpVps`.
5. Desplegar el backend.
6. Probar de punta a punta (§12).

Los pasos 2 y 3 pueden ir antes del 5 sin problema: nada usa la tabla hasta que
exista el controller.

---

## 12. Verificación

**Solo catálogo** (no toca Cloudflare) — reemplazar `<userId>`:

```sql
EXEC sp_ReserveDnsRecord
     @UserId = <userId>, @Label = 'airflow', @Cell = 'idempotencia',
     @ZoneName = 'coderhivex.com', @RecordType = 'A',
     @Content = '203.0.113.10', @Proxied = 1, @Ttl = 1;

EXEC sp_ConfirmDnsRecord   @DnsRecordId = <id>, @ProviderRecordId = 'prueba-local';
EXEC sp_GetUserDnsRecords  @UserId = <userId>;
EXEC sp_UpdateDnsRecord    @DnsRecordId = <id>, @UserId = <userId>, @Content = '198.51.100.5';
EXEC sp_GetAllDnsRecords   @Cell = 'idempotencia';
EXEC sp_RevokeDnsRecord    @DnsRecordId = <id>, @RevokedByUserId = <adminId>,
                           @Reason = 'Prueba de revocación.';
```

`203.0.113.0/24` y `198.51.100.0/24` son rangos de documentación: sirven para la
prueba del catálogo, pero **el backend los rechaza** como destino real (ver
`DTOs/IpAddressRules.cs`).

**Punta a punta** (sí crea el registro real):

```http
POST /dns
Authorization: Bearer <jwt>
Content-Type: application/json

{ "label": "airflow", "cell": "idempotencia", "ipAddress": "203.0.113.10" }
```

Esperado: `201` con `fqdn = "airflow.idempotencia.coderhivex.com"`. Luego:

```bash
dig +short airflow.idempotencia.coderhivex.com
# proxeado: devuelve IPs de Cloudflare, NO la del usuario. Eso es correcto.

curl -sI https://airflow.idempotencia.coderhivex.com | head -1
# si da error de certificado, revisar Total TLS (§7)
```

---

## 13. Revertir

Solo en entornos de prueba:

```sql
DROP PROCEDURE IF EXISTS sp_ReserveDnsRecord, sp_ConfirmDnsRecord,
     sp_FailDnsRecord, sp_GetUserDnsRecords, sp_GetDnsRecordDetail,
     sp_UpdateDnsRecord, sp_DeleteDnsRecord, sp_GetAllDnsRecords,
     sp_GetDnsRecordDetailAdmin, sp_RevokeDnsRecord;
DROP TABLE IF EXISTS dbo.DnsRecords;
DROP TABLE IF EXISTS dbo.DnsReservedLabels;
```

**Los registros que ya existan en Cloudflare NO se borran con esto.** Hay que
eliminarlos antes, o quedan resolviendo sin nadie que los administre y sin forma
de saber a quién pertenecían.

---

## 14. Deuda conocida: la célula no valida pertenencia

Hoy **no se valida que el usuario pertenezca a la célula que declara**. No existe
todavía un catálogo de células ni una relación usuario↔célula en la base, así que
cualquier usuario autenticado puede crear un subdominio bajo el nombre de
cualquier célula.

Es una decisión consciente para no bloquear esta entrega, pero **no es el estado
final**. Mientras siga así, el control es a posteriori: `GET /admin/dns?cell=…`
lista lo que hay bajo cada célula y `POST /admin/dns/{id}/revoke` lo quita.

Cuando exista el catálogo, el cambio es acotado: una tabla `Celulas` (+ membresía
o columna en `Users`) y una validación más dentro de `sp_ReserveDnsRecord`.
**Nada del backend cambia** — la célula ya viaja en el request y ya se persiste
en su propia columna.
