/* ============================================================================
   sp_ReactivateDatabase — endpoint POST /databases/{id}/reactivate
   ----------------------------------------------------------------------------
   Hasta ahora desactivar era un camino de una sola dirección: una vez
   'Inactive', la única salida era eliminar la BD. Los datos nunca se borraban
   al desactivar (solo se revoca el acceso del login), así que reactivar es
   perfectamente posible y era una funcionalidad faltante, no una limitación
   técnica.

   IMPORTANTE: ejecutar DESPUÉS de 2026-07-29-fix-ck-provdb-status.sql. Este SP
   escribe 'Active', que sí está permitido por la constraint actual, pero el
   flujo completo no sirve de nada si desactivar sigue roto — no se puede
   reactivar algo que nunca llegó a 'Inactive'.

   Idempotente: usa CREATE OR ALTER.
   ========================================================================== */

USE master;
GO

CREATE OR ALTER PROCEDURE sp_ReactivateDatabase
    @DatabaseId INT,
    @UserId     INT
AS
BEGIN
    SET NOCOUNT ON;

    /* Misma guarda y mismo estilo de mensaje que sp_DeactivateDatabase, con el
       estado invertido. Colapsa a propósito tres casos en un solo mensaje (no
       existe / no es tuya / no está inactiva) para no revelarle a un usuario si
       un DatabaseId ajeno existe — misma política que NotFoundException en el
       backend. */
    IF NOT EXISTS (
        SELECT 1 FROM ProvisionedDatabases
        WHERE DatabaseId = @DatabaseId AND UserId = @UserId AND Status = 'Inactive'
    )
    BEGIN
        THROW 50011, 'La base de datos no existe, no te pertenece, o no está inactiva.', 1;
    END

    UPDATE ProvisionedDatabases
    SET Status = 'Active',

        /* PausedAt vuelve a NULL: la columna significa "desde cuándo está
           pausada", y ya no lo está. Dejarla con el valor viejo haría que el
           detalle mostrara una fecha de pausa en una BD activa. */
        PausedAt = NULL,

        /* LastActivityAt SÍ se actualiza acá, a diferencia de
           sp_UpdateDatabaseSize (que a propósito no la toca). La diferencia es
           quién actúa: medir el tamaño lo hace un job del sistema, pero
           reactivar es una acción explícita del estudiante y por lo tanto es
           actividad real suya. Sin esto, el futuro job de TTL (ítem 11) vería
           una BD recién reactivada con LastActivityAt antiguo y la volvería a
           pausar en su siguiente pasada. */
        LastActivityAt = SYSUTCDATETIME()
    WHERE DatabaseId = @DatabaseId AND UserId = @UserId;
END;
GO

/* ----------------------------------------------------------------------------
   Verificación: desactivar y volver a activar una BD de prueba debe dejarla
   con Status='Active' y PausedAt=NULL.

       SELECT DatabaseId, DbName, Status, PausedAt, LastActivityAt
       FROM ProvisionedDatabases
       WHERE DatabaseId = <id>;
   -------------------------------------------------------------------------- */
