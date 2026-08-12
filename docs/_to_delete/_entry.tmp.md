## Sesión 17 — 2026-08-12 (servicio de DNS: subdominios por usuario sobre Cloudflare)

**Pedido original:**

```
hey claude es hora de añadir un nuevo servicio al backend, va a ser para dar
dns, crea una parte en la variable de entorno, estos datos van a ser de prueba
así que no te preocupes no son los de producción real.
[zone id + api token de Cloudflare]
sigue la estructura del proyecto y hazlo con buenas prácticas.
investiga cómo se ensambla bien, se supone que las apis creadas por nuestro
sistema siguen la temática de <label>.idempotencia.<dominio-dns>, vamos a crear
sub dns con esa estructura, pásame lo que se tenga que cambiar en la db en un .md
```

**Decisiones tomadas con el usuario antes de escribir código** (cuatro preguntas
que cambiaban el diseño de raíz, por eso se preguntaron en vez de asumirse):

| Pregunta | Decisión |
|---|---|
| Zona | `andrescortes.dev` → los subdominios quedan como `{label}.idempotencia.andrescortes.dev` |
| Disparador | **Recurso independiente** con CRUD propio en `/dns`, no acoplado a la creación de bases de datos |
| Nombre | **Label libre** elegido por el usuario (validado), sufijo puesto por el backend |
| Tipo de registro | **CNAME** hacia `idempotencia.<zona>`, sin proxy |

El CNAME hacia el registro raíz es la decisión con más consecuencias: si la
plataforma cambia de IP, se actualiza **un** registro y todos los subdominios de
los estudiantes lo siguen solos. Con registros `A` habría que reescribirlos uno
por uno.

El proxy queda apagado a propósito: el proxy de Cloudflare solo enruta HTTP y
HTTPS, así que un subdominio proxeado **no sirve** para conectarse a un motor de
base de datos por su puerto TCP — que es el uso más probable de estos
subdominios dado el resto de la plataforma.

**Qué se hizo:**

- **Configuración** — nueva sección `Dns` en
  [`appsettings.json`](../appsettings.json) enlazada a
  [`Services/DnsSettings.cs`](../Services/DnsSettings.cs): `ZoneId`, `ApiToken`,
  `ZoneName`, `BaseSubdomain`, `DefaultRecordType`, `DefaultTarget`, `Proxied`,
  `TtlSeconds`, `RequestTimeoutSeconds`. Todas sobreescribibles por variable de
  entorno (`Dns__ApiToken`, etc.), que es como debe ir el token en despliegue.
  [`Program.cs`](../Program.cs) valida `ZoneId`/`ApiToken`/`ZoneName` al
  arrancar y **no levanta** si falta alguna — mismo criterio que
  `Provisioning:IpVps` y `Cors:AllowedOrigins`: sin eso, el error recién
  aparecería en el primer `POST /dns` de un usuario real.

- **Capa de dominio, espejando el aprovisionamiento de bases de datos:**
  - [`Interfaces/IDnsProvider.cs`](../Interfaces/IDnsProvider.cs) — adaptador
    hacia el proveedor externo (el análogo de `IDatabaseProvisioner`).
  - [`Interfaces/IDnsRepository.cs`](../Interfaces/IDnsRepository.cs) — solo
    invoca SPs de control.
  - [`Interfaces/IDnsProvisioningService.cs`](../Interfaces/IDnsProvisioningService.cs) —
    orquestador.
  - [`Provisioners/CloudflareDnsProvider.cs`](../Provisioners/CloudflareDnsProvider.cs) —
    `HttpClient` tipado contra la API v4.
  - [`Repository/DnsRepository.cs`](../Repository/DnsRepository.cs),
    [`Services/DnsProvisioningService.cs`](../Services/DnsProvisioningService.cs),
    [`Controllers/DnsController.cs`](../Controllers/DnsController.cs),
    [`DTOs/DnsDtos.cs`](../DTOs/DnsDtos.cs) y 6 modelos en `Models/`.

- **Base de datos** — [`sql/2026-08-12-dns-records.sql`](../sql/2026-08-12-dns-records.sql):
  tablas `DnsRecords` y `DnsReservedLabels`, 3 índices y 7 SPs. El documento de
  cambios está en
  [`docs/cambios-db-dns-2026-08-12.md`](cambios-db-dns-2026-08-12.md).

- **Rate limiting** — política nueva `dns` (10/min por usuario) en las tres
  escrituras. El razonamiento es distinto al de `db-provisioning`: acá el abuso
  relevante no es que el usuario se haga daño a sí mismo sino que **agote la
  cuota de la API de Cloudflare, que es compartida por toda la plataforma**.

**Decisiones de diseño que conviene no revertir sin leer el motivo:**

1. **No hay factory de proveedores de DNS**, a diferencia de
   `IDatabaseProvisionerFactory`. Los motores de BD son cuatro y conviven
   simultáneamente (el usuario elige uno por base); el proveedor de DNS es uno
   solo por despliegue y se elige por configuración. Un factory para una sola
   implementación es indirección sin beneficio. La *interfaz* sí existe, para
   que agregar Route53 mañana no obligue a tocar el orquestador.

2. **El FQDN lo compone el SP y se persiste ya compuesto.** El backend le pasa
   el dominio base a `sp_ReserveDnsRecord`. Si el backend lo recalculara en cada
   operación, un cambio de `Dns:BaseSubdomain` dejaría los registros ya creados
   apuntando a un nombre distinto del que quedó en Cloudflare, y el borrado no
   encontraría nada que borrar.

3. **La unicidad la garantiza un índice único filtrado**
   (`WHERE Status IN ('Provisioning','Active')`), no un `UNIQUE` plano. Con un
   UNIQUE plano, el primer usuario que creara y borrara `ventas` bloquearía ese
   nombre para siempre. La comprobación previa del SP existe solo para dar un
   mensaje entendible en vez de un error 2601 → 500 genérico; la carrera real la
   resuelve el índice.

4. **Borrar no exige desactivar primero**, a diferencia de `/databases`. Ese
   paso protege datos del usuario antes de un borrado irreversible; acá no hay
   datos que proteger y volver a crear el subdominio deshace la operación.

5. **Reconciliación de registros huérfanos.** El caso feo del flujo es: el
   registro se creó en Cloudflare pero `sp_ConfirmDnsRecord` falló, así que el
   catálogo no tiene el `ProviderRecordId` (la confirmación es justamente la que
   lo guarda) y el registro quedaría resolviendo para siempre, bloqueando ese
   nombre sin que nadie pueda liberarlo. Por eso `IDnsProvider` expone
   `FindRecordIdAsync(fqdn)` y tanto la reversión como el borrado lo usan como
   respaldo.

6. **`DeleteAsync` del proveedor es idempotente ante un 404.** Si alguien borró
   el registro a mano desde el panel de Cloudflare, lanzar dejaría al usuario sin
   poder limpiar su propia fila del catálogo.

7. **Los códigos 7000/7003 de Cloudflare NO se tratan como "no existe"**, aunque
   se parezcan: aparecen cuando el `ZoneId` está mal configurado. Tratarlos como
   "ya borrado" convertiría un despliegue roto en un borrado silencioso y
   exitoso — el catálogo se limpiaría mientras los registros reales siguen vivos.

**Qué quedó pendiente:**

- **No se pudo compilar ni verificar en vivo.** El entorno de esta sesión no
  tiene salida a `api.cloudflare.com` (el `curl` de verificación del token da 403
  en el proxy), ni a `api.nuget.org` / los servidores de .NET, así que no hay
  SDK para correr `dotnet build`. **El código está sin compilar**: hay que
  ejecutar `dotnet build` antes de desplegar. Tampoco se verificó que el token
  entregado sea válido ni que la zona `c1c62663d28fa916dc9bc030103e6e83`
  corresponda efectivamente a `andrescortes.dev`.
- Ejecutar `sql/2026-08-12-dns-records.sql` en el catálogo y confirmar que la FK
  `FK_DnsRecords_Users` quedó creada (el script la crea solo si encuentra
  `dbo.Users(UserId)`; si la tabla se llama distinto, avisa con un `PRINT` y hay
  que crearla a mano).
- Confirmar que existe el registro raíz `idempotencia.andrescortes.dev` en la
  zona: es el destino por defecto de todos los CNAME y, si no existe, los
  subdominios se crean pero no resuelven a nada.
- El token está hoy en `appsettings.json` junto al resto de los secretos del
  proyecto (ítem 3 del backlog). Pasarlo a `Dns__ApiToken` por entorno en el
  despliegue.

**Documentos actualizados:** `docs/routes.md` (6 rutas nuevas, hallazgo 13,
total 13 → 19), `docs/API.md` (sección 11 nueva + fila en la tabla de estado de
la sección 10), y este archivo.

---

