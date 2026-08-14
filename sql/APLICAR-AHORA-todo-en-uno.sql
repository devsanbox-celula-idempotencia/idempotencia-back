/* ============================================================================
   APLICAR AHORA — todo lo que le falta al catálogo, en un solo archivo
   ----------------------------------------------------------------------------
   Consolida lo que quedó pendiente de:
     - sql/2026-08-12-partner-mysql-external-ref.sql  (columnas + 2 SPs)
     - sql/2026-07-29-size-sync.sql                   (2 SPs del job de tamaños)

   Existe para pegarlo tal cual en el cliente SQL que ya tenga acceso al
   catálogo (el mismo donde corrió el diagnóstico), sin depender de sqlcmd, de
   git ni de llegar a la BD desde fuera de la VPN.

   Correr contra el CATÁLOGO (base master), NO contra el servidor de Raft.
   Es idempotente: se puede correr varias veces sin efecto adicional.

   Si el cliente NO entiende 'GO', ejecutar cada bloque numerado por separado.
   ========================================================================== */

USE master;
GO

-- 1 --------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalId')
    ALTER TABLE ProvisionedDatabases ADD ExternalId NVARCHAR(100) NULL;
GO

-- 2 --------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalDbName')
    ALTER TABLE ProvisionedDatabases ADD ExternalDbName NVARCHAR(128) NULL;
GO

-- 3 -- la que falta hoy y rompe la creación de MySQL --------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalLoginName')
    ALTER TABLE ProvisionedDatabases ADD ExternalLoginName NVARCHAR(128) NULL;
GO

-- 4 --------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalMaxStorageMB')
    ALTER TABLE ProvisionedDatabases ADD ExternalMaxStorageMB INT NULL;
GO

-- 5 -- sp_SetDatabaseExternalRef: 5 parámetros (hoy tiene 3) ------------------
CREATE OR ALTER PROCEDURE sp_SetDatabaseExternalRef
    @DatabaseId           INT,
    @ExternalId           NVARCHAR(100),
    @ExternalDbName       NVARCHAR(128),
    @ExternalLoginName    NVARCHAR(128) = NULL,
    @ExternalMaxStorageMB INT           = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ExternalId IS NULL OR LTRIM(RTRIM(@ExternalId)) = ''
        THROW 50030, 'El identificador externo no puede ser vacío.', 1;

    IF @ExternalMaxStorageMB IS NOT NULL AND @ExternalMaxStorageMB <= 0
        THROW 50032, 'La cuota externa de almacenamiento debe ser mayor que cero.', 1;

    UPDATE ProvisionedDatabases
    SET ExternalId           = @ExternalId,
        ExternalDbName       = @ExternalDbName,
        ExternalLoginName    = @ExternalLoginName,
        ExternalMaxStorageMB = @ExternalMaxStorageMB
    WHERE DatabaseId = @DatabaseId
      AND Status <> 'Deleted';

    IF @@ROWCOUNT = 0
        THROW 50031, 'No se encontró una base de datos activa con ese identificador para guardar su referencia externa.', 1;
END;
GO

-- 6 -- sp_GetDatabaseExternalRef: 4 columnas (hoy devuelve 2) -----------------
CREATE OR ALTER PROCEDURE sp_GetDatabaseExternalRef
    @DatabaseId INT,
    @UserId     INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  pd.ExternalId,
            pd.ExternalDbName,
            pd.ExternalLoginName,
            pd.ExternalMaxStorageMB
    FROM ProvisionedDatabases pd
    WHERE pd.DatabaseId = @DatabaseId
      AND pd.UserId     = @UserId;
END;
GO

-- 7 -- job de tamaños: no existe todavía --------------------------------------
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

-- 8 --------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE sp_UpdateDatabaseSize
    @DatabaseId    INT,
    @CurrentSizeMB DECIMAL(10,2)
AS
BEGIN
    SET NOCOUNT ON;

    IF @CurrentSizeMB < 0
        THROW 50020, 'El tamaño medido no puede ser negativo.', 1;

    UPDATE ProvisionedDatabases
    SET CurrentSizeMB = @CurrentSizeMB
    WHERE DatabaseId = @DatabaseId
      AND Status <> 'Deleted';
END;
GO

-- 9 -- verificación: 4 columnas y 5 parámetros --------------------------------
SELECT 'columnas' AS chequeo, name AS detalle FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name LIKE 'External%'
UNION ALL
SELECT 'parametros', name FROM sys.parameters
WHERE object_id = OBJECT_ID('dbo.sp_SetDatabaseExternalRef')
UNION ALL
SELECT 'SPs', name FROM sys.procedures
WHERE name IN ('sp_SetDatabaseExternalRef','sp_GetDatabaseExternalRef',
               'sp_GetDatabasesForSizeSync','sp_UpdateDatabaseSize');
GO
