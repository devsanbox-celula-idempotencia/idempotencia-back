---
id: seguridad-y-pendientes
title: Seguridad y Pendientes Conocidos
sidebar_position: 7
sidebar_label: Seguridad y Pendientes
---

# Seguridad y pendientes conocidos

Resumen orientado a consumidores de la API y a quienes desplieguen el
backend. El detalle técnico completo (líneas de código, SQL propuesto, etc.)
vive en `docs/bugs.md` dentro del repositorio.

**Convención de estado:** 🔴 Abierto · 🟡 Fix entregado sin confirmar · 🟢
Resuelto (confirmado) · 🔵 Resuelto parcialmente.

## Alta severidad

| Hallazgo | Estado | Resumen |
|---|---|---|
| Token JWT en la query string del redirect OAuth | 🔴 Abierto | El JWT viaja como parámetro de URL en el callback OAuth, exponible en historial del navegador y logs. Ver [Autenticación](./autenticacion). |
| Redirect OAuth también expone `email`/`fullName`/`role`/`userId` | 🔴 Abierto | Amplía el hallazgo anterior — mismo fix pendiente (código de un solo uso canjeable por `POST`). |
| Ciclo de vida (TTL) sin implementar | 🔴 Abierto | No hay pausado/eliminación automática de BDs inactivas todavía. Ver [Aprovisionamiento de bases de datos](./aprovisionamiento-bases-de-datos). |
| Secretos reales en texto plano en `appsettings.json` | 🔴 Abierto | Ver [Configuración por ambiente](./configuracion-ambientes) — pendiente mover a User Secrets / vault. |
| Aislamiento entre usuarios en MySQL | 🔵 Parcial | SQL Server/Postgres/Mongo ya corregidos; MySQL tiene una limitación inherente del motor (nombres de otras BDs visibles, no sus datos). |

## Severidad media

| Hallazgo | Estado | Resumen |
|---|---|---|
| Cuota de almacenamiento no aplicada en Postgres/MySQL/Mongo | 🔴 Abierto | Solo SQL Server hace cumplir `MaxStorageMB` de forma nativa. |
| Límite de conexiones concurrentes sin equivalente nativo en SQL Server/Mongo | 🔵 Parcial | MySQL/Postgres ya resueltos y configurables por request. |
| Dependencia `Microsoft.OpenApi` con vulnerabilidad conocida | 🔴 Abierto | Alta severidad reportada por `dotnet build` (advisory `GHSA-v5pm-xwqc-g5wc`). |
| Sin rate limit dedicado en endpoints OAuth | 🔴 Abierto | Solo cubiertos por el límite global (100/min/IP), 10× más permisivo que `auth`. |

## Baja severidad / cosmético

| Hallazgo | Estado | Resumen |
|---|---|---|
| `CurrentSizeMB` sin tipo de columna explícito en EF Core | 🔴 Abierto | Riesgo de truncamiento silencioso de decimales. |
| Claim `"UserId"` duplicado como string literal | 🔴 Abierto | Sin constante compartida entre emisor y consumidor del claim. |
| `idempotencia.http` referencia un endpoint inexistente | 🔴 Abierto | Archivo de prueba obsoleto (`/weatherforecast/` de la plantilla por defecto). |

## Ya resuelto (para contexto)

- **Enumeración de cuentas OAuth-only en `POST /auth/login`** 🟢 — el login
  ya responde siempre el mismo mensaje genérico (`"Credenciales
  inválidas."`) sin importar si el correo no existe, el password es
  incorrecto, o la cuenta es solo-OAuth.
- **BD MySQL huérfana en login OAuth** 🟢 — se quitó el
  auto-aprovisionamiento de `ExternalLoginAsync`; el frontend debe pedirla
  explícitamente (ver [Autenticación](./autenticacion)).
- **`sp_GetLoginByEmail` referenciaba una tabla `Roles` inexistente** 🟢 —
  confirmado corregido en vivo contra la base de datos real.

## Nota sobre la base de datos de catálogo

Los Stored Procedures que sostienen la lógica de negocio **no están
versionados en este repositorio** (arquitectura *database-centric*, ver
[Visión general](./vision-general)). Cualquier cambio de esquema debe
verificarse manualmente contra el servidor real antes de asumir que un
endpoint funciona de punta a punta en un ambiente nuevo.
