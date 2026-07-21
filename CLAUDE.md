# idempotencia-back

Backend .NET 10 + EF Core, arquitectura *database-centric*: la lógica de
negocio vive en Stored Procedures; el backend es un mediador HTTP (JWT +
OAuth) que los invoca. Ver [`docs/API.md`](docs/API.md) para la guía de
consumo del frontend.

## Mantenimiento de documentación de rutas

Cualquier cambio en `Controllers/` (nueva ruta, ruta eliminada, cambio de
método HTTP, cambio de auth/rate-limit, cambio de estado funcional) debe
reflejarse en el mismo cambio en:

- [`docs/routes.md`](docs/routes.md) — tabla completa de rutas escaneadas y su
  estado funcional.
- [`docs/API.md`](docs/API.md) — guía de consumo para el frontend.

Si se descubre un bug o comportamiento inesperado durante ese trabajo,
regístralo en [`docs/bugs.md`](docs/bugs.md) con tipo de problema y solución
propuesta, en vez de dejarlo solo mencionado en el chat.
