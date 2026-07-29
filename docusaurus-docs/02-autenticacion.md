---
id: autenticacion
title: Autenticación
sidebar_position: 2
sidebar_label: Autenticación
---

# Autenticación

El backend soporta dos mecanismos de inicio de sesión: **por contraseña**
(email + password propios) y **OAuth 2.0** contra Google y GitHub. Ambos
terminan en la emisión del mismo tipo de token: un **JWT firmado por el
backend**.

## JSON Web Token (JWT)

- Emitido por `JwtTokenService`, firmado con clave simétrica (`Jwt:Key` en
  configuración) y validado en cada request protegido vía
  `Microsoft.AspNetCore.Authentication.JwtBearer`.
- Duración configurable (`Jwt:ExpirationMinutes`, hoy 60 minutos).
- Claims incluidos: `sub` / `UserId`, `email`, `role` (`Admin`, `Student` o
  `Developer`). **No** incluye `fullName`.
- Se envía en cada request protegido como:
  ```
  Authorization: Bearer <token>
  ```
- Si el token falta, es inválido o expiró, el middleware de autenticación
  corta la petición **antes** de llegar al controller y responde `401` con
  cuerpo vacío (no usa el formato uniforme `{status, error}` del resto de la
  API).

## Login por contraseña

`POST /auth/register` y `POST /auth/login` (ver [Referencia de la API](./api-referencia)
para el detalle completo de bodies y errores). Las contraseñas se hashean con
**BCrypt** antes de persistirse; el hash nunca se serializa en ninguna
respuesta.

Al primer registro o primer login exitoso **por contraseña**, el backend
aprovisiona automáticamente una base de datos **MySQL** a nombre del usuario
(`AuthService.EnsureMySqlDatabaseAsync`). Es idempotente — si el usuario ya
tiene una BD MySQL, no crea otra — y **no bloquea el login si falla** (se
registra el error y el campo queda `null`; el usuario puede pedirla luego con
`POST /databases`).

## Login con Google / GitHub (OAuth 2.0)

El flujo completo lo maneja el backend; el frontend solo redirige el
navegador a un endpoint de inicio.

```
1. El frontend redirige el navegador a  GET /auth/{provider}/login
2. El backend redirige a la pantalla de consentimiento del proveedor
3. El usuario acepta
4. El proveedor llama de vuelta al callback interno del backend
   (Google: /signin-google · GitHub: callback propio de AspNet.Security.OAuth.GitHub)
5. El backend resuelve la identidad (vía la cookie temporal "External"),
   invoca sp_UpsertExternalLogin y firma el JWT
6. El backend redirige al frontend con los datos de sesión en la query string
```

Configuración en `Program.cs`:

- `AddGoogle("Google", ...)` — usa `Authentication:Google:ClientId` /
  `ClientSecret` desde configuración (nunca hardcodeado). `SignInScheme =
  "External"`.
- `AddGitHub("GitHub", ...)` — mismo patrón, además pide el scope
  `user:email` porque GitHub puede devolver el correo como privado (el
  handler llama a `/user/emails` cuando aplica).
- Cookie temporal `"External"` (`Colmena.External`), vida de 5 minutos —
  solo almacena la identidad externa mientras se resuelve el callback, no es
  la sesión final del usuario.

### Redirect al frontend tras el callback

El backend redirige a:

```
{Frontend:BaseUrl}/oauth/callback?token=...&expiresAt=...&userId=...&email=...&fullName=...&role=...
```

o, si algo falla, a `{Frontend:BaseUrl}/oauth/callback?error=<mensaje>`.
`Frontend:BaseUrl` es configuración por ambiente (ver
[Configuración por ambiente](./configuracion-ambientes)).

:::caution Nota de seguridad — pendiente
El JWT y los datos del usuario (`email`, `fullName`, `role`, `userId`) viajan
como **parámetros de query string**, no en el fragmento (`#`) ni por `POST`.
Esto los expone al historial del navegador, a logs de acceso del
servidor/proxy, y al header `Referer` si la página de callback carga recursos
de terceros. Es un hallazgo de seguridad conocido y abierto — ver
[Seguridad y pendientes](./seguridad-y-pendientes). **Mientras no se corrija,
el frontend debe leer los parámetros y llamar inmediatamente
`history.replaceState(...)`** para no dejarlos en el historial del navegador.
:::

### OAuth no aprovisiona la BD MySQL automáticamente

A diferencia del login por contraseña, un primer login por Google/GitHub
**no** crea la BD MySQL automáticamente (se removió deliberadamente: como la
respuesta OAuth viaja por redirect y no como JSON, la contraseña generada se
perdía sin que el usuario la viera nunca). El frontend debe, tras resolver el
callback, llamar `GET /databases` y si viene vacío, pedir explícitamente
`POST /databases` con `{"engine": "MySql", "dbName": "principal"}`.

## Configurar el redirect URI de OAuth en Google Cloud Console

Para que `GET /auth/google/login` funcione en cualquier ambiente (local, QA,
producción), el **Authorized redirect URI** configurado en el proyecto de
Google Cloud Console debe coincidir **exactamente** (esquema, host, path, sin
slash de más o de menos) con el callback interno que genera
`AspNet.Security`/`Microsoft.AspNetCore.Authentication.Google` para ese host:

```
https://<host-del-ambiente>/signin-google
```

Puntos a verificar por ambiente:

- El dominio del ambiente (ej. `docs.idempotencia.andrescortes.dev` en QA)
  debe estar en **Authorized domains** de la pantalla de consentimiento OAuth
  del proyecto de Google Cloud.
- Si el proyecto de Google Cloud sigue en modo **Testing**, solo los correos
  agregados como **Test users** pueden autenticarse — cualquier otra cuenta
  recibe `Error 400: invalid_request` (no cumple la política de OAuth 2.0).
- Cada ambiente (Development / QA / Producción) puede necesitar su propio
  `ClientId`/`ClientSecret` si usan client IDs de OAuth separados, o bien
  agregar **todos** los redirect URIs de todos los ambientes al mismo client
  ID si se comparte uno solo.
