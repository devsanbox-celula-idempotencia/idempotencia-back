# Cambios en la API — 30 de julio de 2026

Para el equipo de frontend. Dos cambios, **ninguno rompe código existente**:
uno agrega campos, el otro cambia el *valor* de un campo que ya existía.

| # | Cambio | ¿Rompe algo? |
|---|---|---|
| 1 | Campos nuevos `connectionUri` y `jdbcUrl` al crear una BD y en el login | No, es aditivo |
| 2 | El campo `host` deja de traer el nombre del contenedor y pasa a traer la IP del servidor | No, pero si lo tenían cacheado/hardcodeado, cambia |

La URL base, la autenticación, los códigos de estado y el formato de las
respuestas de error (`{ "status": ..., "error": ... }`) **no cambiaron**.

---

## 1. Campos nuevos: `connectionUri` y `jdbcUrl`

### Qué cambió

`POST /databases` y el `mySqlDatabase` del login ahora devuelven, además de
`host`/`port`/`loginName`/`password`, la conexión **ya armada** en dos formatos:

```json
{
  "databaseId": 5,
  "engine": "MySql",
  "dbName": "colmena_u12_principal",
  "host": "100.99.206.50",
  "port": 3306,
  "loginName": "usr_colmena_u12_principal",
  "password": "P4ssGeneradaUnaVez",
  "connectionUri": "mysql://usr_colmena_u12_principal:P4ssGeneradaUnaVez@100.99.206.50:3306/colmena_u12_principal?ssl-mode=REQUIRED",
  "jdbcUrl": "jdbc:mysql://100.99.206.50:3306/colmena_u12_principal?sslMode=REQUIRED"
}
```

- **`connectionUri`** — formato nativo del motor, **con las credenciales
  dentro**. Para clientes de consola y herramientas que aceptan una cadena
  completa. En SQL Server no es una URI sino la cadena de keywords de ADO.NET
  (`Server=...;Database=...;`), que es lo que aceptan SSMS y Azure Data Studio.
- **`jdbcUrl`** — la misma conexión para clientes de escritorio Java (DBeaver,
  MySQL Workbench, DataGrip → opción "conectar por URL"), **sin** credenciales,
  porque esos clientes las piden en campos aparte. Es **`null` en Mongo**, que no
  tiene driver JDBC estándar: no lo muestren en ese caso.

### Por qué existe

Sin la cadena armada, el usuario tenía que configurar el cifrado a mano en su
gestor. En MySQL eso se veía como un error de conexión que solo se resolvía
activando `allowPublicKeyRetrieval` — un paso extra, poco descubrible y además
peor para la seguridad. Ahora la cadena ya trae el parámetro de cifrado correcto
de cada motor y el usuario solo copia y pega.

### Qué hacer en el front

- Mostrar ambas cadenas en la pantalla de credenciales, con **botón de copiar**
  (son largas; no se esperan tipeadas).
- Ocultar la fila de `jdbcUrl` cuando venga `null`.
- ⚠️ **`connectionUri` contiene la contraseña.** Trátenla con el mismo cuidado
  que `password`: no loguearla en consola, no mandarla a analytics, no
  persistirla en su propio backend. Como `password`, se entrega **una sola vez**.
- `GET /databases/{id}` **no** trae estos campos, por diseño: ahí no hay
  contraseña que poner (no se puede recuperar, solo se guarda el hash). Si el
  usuario perdió la cadena, el camino es
  `POST /databases/{id}/reset-password` — el correo que llega ya incluye la
  cadena nueva completa.

---

## 2. El valor de `host` cambia

### Qué cambió

Nada en el contrato: el campo `host` sigue siendo `host`, un string. Lo que
cambia es lo que trae. Antes salía de una clave de configuración que, en el
servidor desplegado, tenía el **nombre del contenedor de Docker**
(`idempotencia-mysql`, `idempotencia-postgres`, …) — un nombre que solo resuelve
dentro de la red interna del servidor, así que el usuario no podía conectarse con
él. Ahora trae la IP pública del servidor de bases de datos.

### Qué hacer en el front

Nada, si lo estaban leyendo de la respuesta. Solo revisen que no haya quedado
ningún host de BD hardcodeado o cacheado de una respuesta vieja.

---

## Contexto

Detalle técnico completo en `docs/bugs.md`, ítems 27 (el `host`) y 28 (el
cifrado), y en la bitácora `docs/claude.md`, sesiones 15 y 16. Ambos cambios
están en código pero **pendientes de desplegar** al momento de escribir esto.
