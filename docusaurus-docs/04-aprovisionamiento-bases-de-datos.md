---
id: aprovisionamiento-bases-de-datos
title: Aprovisionamiento de Bases de Datos
sidebar_position: 4
sidebar_label: Aprovisionamiento de BD
---

# Aprovisionamiento de bases de datos

Cada usuario puede tener una o más bases de datos físicas, aprovisionadas a
demanda vía `POST /databases` (o automáticamente en el primer login por
contraseña — ver [Autenticación](./autenticacion)).

## Motores soportados

Los 4 motores tienen provisioner real implementado (creación de
usuario/rol + base de datos + permisos acotados a esa BD, nunca privilegios
globales):

| Motor | Puerto por defecto | Provisioner |
|---|---|---|
| SQL Server | 1433 | `SqlServerProvisioner` |
| PostgreSQL | 5432 | `PostgresProvisioner` |
| MySQL | 3306 | `MySqlProvisioner` |
| MongoDB | 27017 | `MongoProvisioner` |

## Cuota de almacenamiento (`MaxStorageMB`)

- **SQL Server**: aplicada nativamente con `MAXSIZE = {maxStorageMb}MB` en el
  `CREATE DATABASE` — el motor mismo rechaza escrituras que excedan el tope.
- **PostgreSQL / MySQL / MongoDB**: estos motores **no tienen un equivalente
  nativo** de tamaño máximo por base de datos. No se hace cumplir todavía en
  producción para estos tres motores (ver [Seguridad y pendientes](./seguridad-y-pendientes)).

## Límite de conexiones concurrentes (`maxConcurrentConnections`)

Configurable por request al crear una BD (`CreateDatabaseRequest.MaxConcurrentConnections`,
opcional, rango 1–100). El backend resuelve el valor final así:

1. Si el cliente no pide nada → usa el default configurado por motor
   (`Provisioning:{Engine}:MaxConcurrentConnections`, hoy `5`).
2. Si el cliente pide un valor → se acota **siempre** a
   `Provisioning:{Engine}:MaxConcurrentConnectionsCap` (hoy `20`). El cliente
   nunca puede desactivar el control pidiendo un número arbitrariamente alto.

| Motor | Mecanismo nativo | Estado |
|---|---|---|
| MySQL | `MAX_USER_CONNECTIONS` | ✅ Aplicado |
| PostgreSQL | `CONNECTION LIMIT` | ✅ Aplicado |
| SQL Server | No existe límite nativo por login (requeriría *Resource Governor* o un logon trigger) | 🔴 Sin implementar |
| MongoDB | No existe límite por usuario (solo `net.maxIncomingConnections` a nivel de servidor) | 🔴 Sin implementar |

El valor efectivamente aplicado vuelve en la respuesta
(`CreateDatabaseResponse.MaxConcurrentConnections`); vale `0` en SqlServer/Mongo
porque ahí no se aplica.

## Aislamiento entre bases de datos de distintos usuarios

Cada estudiante tiene su propia base, pero por defecto ni SQL Server ni
Postgres restringen qué bases puede *ver* o *a cuáles conectarse* un
login/rol nuevo — solo lo que puede hacer una vez adentro. Mitigaciones
aplicadas:

- **SQL Server**: `DENY VIEW ANY DATABASE` al login nuevo, justo después de
  crearlo — oculta los nombres de las demás bases del servidor.
- **PostgreSQL**: `REVOKE CONNECT ... FROM PUBLIC` + `GRANT CONNECT ... TO`
  el dueño, justo después de crear la BD — ningún otro rol puede conectarse.
- **MongoDB**: no requirió cambios — los roles ya vienen *scoped* a una sola
  base por diseño del provisioner.
- **MySQL**: limitación conocida e inherente del motor — cualquier usuario
  autenticado puede ver los *nombres* de todas las bases del servidor
  (`SHOW DATABASES`, `information_schema.schemata`), aunque no puede leer sus
  datos. No hay forma estándar de ocultarlo sin vistas personalizadas sobre
  `information_schema`.

## Ciclo de vida (TTL) — pausado/eliminación automática por inactividad

El catálogo ya tiene las columnas necesarias (`LastActivityAt`, `PausedAt`,
`DeletedAt` en `ProvisionedDatabaseInfo`), pero **todavía no existe ningún
job** que las actualice o que pause/elimine bases inactivas. Es el ítem de
mayor esfuerzo pendiente del checklist de seguridad — ver
[Seguridad y pendientes](./seguridad-y-pendientes) para el diseño propuesto.
