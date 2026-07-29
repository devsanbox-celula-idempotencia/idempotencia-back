/* ============================================================================
   Sincronización de CurrentSizeMB — docs/bugs.md ítem 25
   ----------------------------------------------------------------------------
   Hasta ahora ProvisionedDatabases.CurrentSizeMB se fijaba en la creación y no
   volvía a cambiar nunca: ningún SP la escribía y no había job que midiera. El
   catálogo vive en SQL Server y no puede medir bases de MySQL/PostgreSQL/Mongo,
   así que la medición la hace el backend (IDatabaseProvisioner.GetSizeMbAsync,
   una implementación por motor) y la persiste con sp_UpdateDatabaseSize.

   Estos dos SPs son lo único que hay que crear en la BD. En los motores de los
   estudiantes (MySQL, Postgres, Mongo) NO se crea nada: el backend solo les
   lanza una consulta de tamaño con la conexión admin que ya tenía.

   Ejecutar en la instancia del catálogo. Es idempotente: se puede correr varias
   veces sin efecto adicional.
   ========================================================================== */

USE master;
GO

/* ----------------------------------------------------------------------------
   1. sp_GetDatabasesForSizeSync
   Devuelve TODAS las bases activas de TODOS los usuarios, para que el job las
   recorra. Los SPs existentes no sirven para esto: sp_GetUserDatabases filtra
   por @UserId y sp_GetDatabaseDetail por (@DatabaseId, @UserId), y el job no
   actúa en nombre de ningún usuario.

   Devuelve exactamente las mismas columnas que sp_GetUserDatabases, en el mismo
   orden, para que EF Core lo mapee sobre el tipo ProvisionedDatabaseInfo que ya
   existe, sin necesidad de un modelo nuevo.

   Solo 'Active': una BD Inactive tiene el login revocado y no puede crecer, y
   una Deleted ya no existe en el motor. Medirlas sería gastar una conexión por
   nada.
   -------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE sp_GetDatabasesForSizeSync
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  pd.DatabaseId, pd.UserId, pd.Engine, pd.DbName, pd.Status,
            pd.MaxStorageMB, pd.CurrentSizeMB, pd.LastActivityAt, pd.CreatedAt,
            pd.PausedAt, pd.DeletedAt
    FROM ProvisionedDatabases pd
    WHERE pd.Status = 'Active'
    ORDER BY pd.DatabaseId;
END;
GO

/* ----------------------------------------------------------------------------
   2. sp_UpdateDatabaseSize
   Persiste el tamaño medido por el backend.

   NO recibe @UserId a propósito: lo llama un job del sistema, no un usuario en
   nombre propio, así que no hay ownership que validar. Todos los demás SPs del
   catálogo sí lo exigen porque nacen de un request autenticado.

   NO toca LastActivityAt: esa columna representa actividad del ESTUDIANTE
   (ver ítem 11, todavía sin implementar). Que un job mida el tamaño no es
   actividad del estudiante; escribirla acá haría que ninguna BD pareciera nunca
   inactiva y rompería el futuro job de TTL antes de existir.

   Guarda contra 'Deleted' para no revivir con datos una fila ya borrada, en
   caso de que el job venga con una medición en vuelo de un ciclo anterior.
   -------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE sp_UpdateDatabaseSize
    @DatabaseId    INT,
    @CurrentSizeMB DECIMAL(10,2)
AS
BEGIN
    SET NOCOUNT ON;

    IF @CurrentSizeMB < 0
    BEGIN
        THROW 50020, 'El tamaño medido no puede ser negativo.', 1;
    END

    UPDATE ProvisionedDatabases
    SET CurrentSizeMB = @CurrentSizeMB
    WHERE DatabaseId = @DatabaseId
      AND Status <> 'Deleted';
END;
GO

/* ----------------------------------------------------------------------------
   Verificación rápida tras desplegar el backend y esperar un ciclo del job:

       SELECT DatabaseId, Engine, DbName, MaxStorageMB, CurrentSizeMB
       FROM ProvisionedDatabases
       WHERE Status = 'Active'
       ORDER BY DatabaseId;

   Si CurrentSizeMB sigue en 0.00 en TODAS las filas, el job no corrió: revisar
   que Provisioning:SizeMonitor:Enabled no esté en false y buscar en los logs la
   línea "Sincronización de tamaños".
   -------------------------------------------------------------------------- */
