---
id: configuracion-ambientes
title: Configuración por Ambiente
sidebar_position: 6
sidebar_label: Configuración por Ambiente
---

# Configuración por ambiente

La configuración sigue el esquema estándar de ASP.NET Core:
`appsettings.json` (base, todos los ambientes) +
`appsettings.{Environment}.json` (overrides), más variables de entorno.
`ASPNETCORE_ENVIRONMENT` determina cuál se carga (`Development`, y los que se
definan para `QA`/`Production`).

:::danger Secretos — no versionar en texto plano
`appsettings.json` hoy contiene, en texto plano, la contraseña del usuario
`sa` de SQL Server, la clave de firma JWT, los `ClientSecret` de Google y
GitHub OAuth, y las credenciales de administrador de MySQL/MongoDB. Es un
hallazgo de seguridad **abierto** (ver [Seguridad y pendientes](./seguridad-y-pendientes)).
La recomendación es mover todo esto a **User Secrets** en desarrollo y a
variables de entorno / un vault en QA y producción, dejando en
`appsettings.json` solo la estructura con placeholders. Los valores reales no
se documentan aquí por ese mismo motivo.
:::

## Secciones de configuración

| Sección | Qué controla |
|---|---|
| `Frontend:BaseUrl` | A dónde redirige el backend tras un login OAuth exitoso (`{BaseUrl}/oauth/callback`) |
| `Cors:AllowedOrigins` | Lista de orígenes permitidos por CORS. El backend **lanza un error al arrancar** si esta lista está vacía, en vez de fallar en silencio |
| `ConnectionStrings:Colmena` | Cadena de conexión a la base de datos de catálogo (SQL Server) |
| `Jwt:Issuer` / `Jwt:Audience` / `Jwt:Key` / `Jwt:ExpirationMinutes` | Configuración de emisión y validación del JWT propio |
| `Authentication:Google:ClientId` / `ClientSecret` | Credenciales de la app OAuth de Google |
| `Authentication:GitHub:ClientId` / `ClientSecret` | Credenciales de la app OAuth de GitHub |
| `Provisioning:IpVps` | **Host público** (IP del VPS, o el dominio que apunte a él) que se entrega a los usuarios en el campo `host` para conectarse a sus BDs. Un único valor para los cuatro motores. El backend **lanza un error al arrancar** si falta, en vez de reportar `localhost` en silencio |
| `Provisioning:{Engine}:Port` / `AdminConnectionString` | Puerto público de cada motor y cadena de conexión con privilegios de administrador que usa el backend para aprovisionar. `AdminConnectionString` es la ruta **interna** al motor (en Docker, el nombre del contenedor) y por eso no sirve como `host` del usuario — para eso está `Provisioning:IpVps` |
| `Provisioning:{Engine}:RequireTls` | Si el motor tiene TLS habilitado y se exige. Con `true`: las cadenas de conexión que se le entregan al usuario incluyen el parámetro de cifrado del motor, y en MySQL los usuarios se crean con `REQUIRE SSL` (el motor rechaza conexiones sin cifrar). Hoy `true` en MySQL y SqlServer; `false` en Postgres y Mongo, cuyos contenedores todavía no tienen certificado — **prenderlo contra un motor sin TLS deja a los usuarios sin poder conectarse** |
| `Provisioning:{Engine}:MaxConcurrentConnections` / `MaxConcurrentConnectionsCap` | Default y tope duro de conexiones concurrentes por BD aprovisionada (MySQL/Postgres) |

## Ambientes

### Development (local)

- `ASPNETCORE_ENVIRONMENT=Development` (definido en `Properties/launchSettings.json`).
- URLs locales: `https://localhost:7113` (HTTPS) y `http://localhost:5175` (HTTP).
- `appsettings.Development.json` solo overridea logging — el resto de la
  configuración de desarrollo vive en el `appsettings.json` base.
- Expone `/openapi/v1.json`, `/swagger` y `/scalar` (deshabilitados fuera de
  `Development`).
- El certificado HTTPS es autofirmado; si el cliente lo rechaza, se puede
  usar el puerto HTTP solo para pruebas (login/registro por contraseña
  funcionan en ambos, pero **OAuth necesita HTTPS**).

### QA / Producción

No hay un `appsettings.QA.json` ni `appsettings.Production.json` en el
repositorio todavía — para desplegar en un ambiente de QA o producción
propio, la práctica recomendada es:

1. Crear `appsettings.{Ambiente}.json` con los overrides específicos (o usar
   variables de entorno equivalentes), **sin** commitear secretos reales.
2. Ajustar `Cors:AllowedOrigins` y `Frontend:BaseUrl` al dominio real del
   frontend de ese ambiente.
3. Setear `Provisioning:IpVps` a la **IP pública del servidor de bases de
   datos** (o su dominio) y `Provisioning:{Engine}:Port` a los puertos
   publicados hacia afuera. En local se usa `"localhost"`. Es lo único que ve
   el usuario final para conectarse: si queda apuntando al nombre de un
   contenedor o a `localhost`, las credenciales entregadas no sirven desde
   fuera del servidor. El backend no arranca si esta clave falta.
4. Configurar el redirect URI de OAuth para el dominio de ese ambiente en
   Google Cloud Console / GitHub OAuth Apps (ver siguiente sección).
5. Si el despliegue va detrás de un reverse proxy, habilitar
   `ForwardedHeaders` (`X-Forwarded-For`) — de lo contrario el rate limiting
   (particionado por IP) colapsa porque todas las peticiones comparten la IP
   del proxy.

## Redirect URIs de OAuth por ambiente

Cada ambiente donde corra el backend necesita su propio **Authorized redirect
URI** dado de alta en el proveedor OAuth, con el dominio exacto de ese
ambiente:

```
https://<host-del-ambiente>/signin-google   (Google)
https://<host-del-ambiente>/signin-github   (GitHub, según configuración del handler)
```

Checklist al agregar un ambiente nuevo (ej. QA):

- [ ] El dominio exacto (sin slash final de más/menos) está en **Authorized
      redirect URIs** del client ID de Google Cloud Console.
- [ ] El dominio (o su dominio raíz) está en **Authorized domains** de la
      pantalla de consentimiento OAuth.
- [ ] Si el proyecto de Google Cloud sigue en modo **Testing**, las cuentas
      que probarán el login están agregadas como **Test users** — de lo
      contrario Google devuelve `Error 400: invalid_request` para cualquier
      cuenta fuera de esa lista.
- [ ] `Cors:AllowedOrigins` incluye el dominio del frontend de ese ambiente.
- [ ] `Frontend:BaseUrl` apunta al frontend correcto para que el redirect
      post-login llegue al lugar esperado.
