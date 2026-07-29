/* ============================================================================
   Fix de CK_ProvDb_Status — docs/bugs.md ítem 24
   ----------------------------------------------------------------------------
   POST /databases/{id}/deactivate falla SIEMPRE con SQL error 547:

       The UPDATE statement conflicted with the CHECK constraint
       "CK_ProvDb_Status" ... column 'Status'.

   Causa: la constraint permite ('Failed','Deleted','Paused','Active',
   'Provisioning') y sp_DeactivateDatabase escribe 'Inactive'.

   El SP tiene razón y la constraint está desactualizada: todo el backend, la
   documentación y el frontend usan 'Inactive' (DeactivateAsync lo asigna,
   DeleteAsync lo exige antes de borrar). El valor 'Paused' viene del
   vocabulario de la propuesta de TTL del ítem 11, que nunca se implementó —
   hoy no hay una sola línea de código que lo escriba.

   Ejecutar ANTES de 2026-07-29-size-sync.sql (este desbloquea funcionalidad
   rota; el otro agrega una nueva).
   ========================================================================== */

USE master;
GO

/* ----------------------------------------------------------------------------
   PASO 1 — Diagnóstico. Ejecuta esto solo y mira el resultado antes de seguir.

   Si aparece alguna fila con Status='Paused', el UPDATE del paso 2 la migrará
   a 'Inactive'. Si no aparece ninguna (lo esperado), el UPDATE es un no-op.
   -------------------------------------------------------------------------- */
SELECT Status, COUNT(*) AS Filas
FROM dbo.ProvisionedDatabases
GROUP BY Status;
GO

/* ----------------------------------------------------------------------------
   PASO 2 — Reemplazar el vocabulario permitido.

   Se quita la constraint primero para poder migrar las filas viejas (si las
   hay) sin que ella misma bloquee el UPDATE, y se vuelve a crear después.
   'Paused' sale porque nadie lo escribe; si algún día se implementa el TTL del
   ítem 11, ese trabajo decidirá si reutiliza 'Inactive' o agrega su propio
   estado.
   -------------------------------------------------------------------------- */
ALTER TABLE dbo.ProvisionedDatabases DROP CONSTRAINT CK_ProvDb_Status;
GO

UPDATE dbo.ProvisionedDatabases
SET Status = 'Inactive'
WHERE Status = 'Paused';
GO

ALTER TABLE dbo.ProvisionedDatabases
ADD CONSTRAINT CK_ProvDb_Status CHECK (
    Status IN ('Provisioning', 'Active', 'Inactive', 'Failed', 'Deleted')
);
GO

/* ----------------------------------------------------------------------------
   PASO 3 — Verificar que ningún otro SP tenga su guarda escrita contra
   'Paused'. Si sp_DeleteDatabase exige Status='Paused', el borrado quedará
   roto igual que la desactivación en cuanto apliques lo de arriba, y lo
   descubrirías hasta el siguiente ciclo de QA.

   Revisa la salida buscando la palabra 'Paused'. Si aparece, cámbiala por
   'Inactive' con un ALTER PROCEDURE.
   -------------------------------------------------------------------------- */
SELECT
    OBJECT_NAME(object_id) AS Procedimiento,
    definition
FROM sys.sql_modules
WHERE definition LIKE '%Paused%'
  AND OBJECT_NAME(object_id) LIKE 'sp_%';
GO

/* ----------------------------------------------------------------------------
   PASO 4 — Confirmación final: la constraint debe listar 'Inactive'.
   -------------------------------------------------------------------------- */
SELECT name, definition
FROM sys.check_constraints
WHERE name = 'CK_ProvDb_Status';
GO

/* ============================================================================
   DESPUÉS DE ESTO, EN LA APLICACIÓN (no en la BD):

   Quedan BDs desincronizadas por los intentos fallidos: DeactivateAsync revoca
   el acceso físico ANTES de tocar el catálogo, así que hay bases con el login
   ya revocado pero marcadas 'Active'. Se reparan volviendo a llamar
   POST /databases/{id}/deactivate sobre ellas — los cuatro provisioners son
   idempotentes en DeactivateAsync (ALTER LOGIN DISABLE sobre un login ya
   deshabilitado, ACCOUNT LOCK sobre un usuario ya bloqueado, NOLOGIN sobre un
   rol ya sin login y updateUser con roles vacíos son todos no-ops exitosos),
   así que reintentar es seguro.
   ========================================================================== */
