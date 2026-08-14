/* ============================================================================
   Diagnóstico: ¿se aplicó 2026-08-12-partner-mysql-external-ref.sql?
   ----------------------------------------------------------------------------
   Correr contra el CATÁLOGO (idempotencia-sqlserver), no contra el servidor de
   Raft. No modifica nada: son cuatro SELECT.

   Lo que se espera ver si el script ya corrió:
     1. servidor/base   -> el servidor del catálogo, base 'master'
     2. columnas        -> 4 filas: ExternalId, ExternalDbName,
                           ExternalLoginName, ExternalMaxStorageMB
     3. parámetros      -> 5 filas para sp_SetDatabaseExternalRef
     4. SPs             -> sp_SetDatabaseExternalRef, sp_GetDatabaseExternalRef,
                           sp_GetDatabasesForSizeSync, sp_UpdateDatabaseSize
   ========================================================================== */

USE master;
GO

-- 1. ¿Estoy en el servidor correcto? Si acá sale el servidor de Raft, el script
--    se aplicó en el lugar equivocado.
SELECT '1. contexto' AS chequeo,
       @@SERVERNAME  AS servidor,
       DB_NAME()     AS base_actual;

-- 2. Columnas externas en ProvisionedDatabases. Faltan si el ALTER TABLE no corrió.
SELECT '2. columnas' AS chequeo, c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('dbo.ProvisionedDatabases')
  AND c.name LIKE 'External%'
ORDER BY c.column_id;

-- 3. Firma real de sp_SetDatabaseExternalRef. Con 3 filas sigue la versión de
--    Mongo -> es exactamente el error "has too many arguments specified".
SELECT '3. parametros' AS chequeo, p.parameter_id, p.name AS parametro,
       TYPE_NAME(p.user_type_id) AS tipo
FROM sys.parameters p
WHERE p.object_id = OBJECT_ID('dbo.sp_SetDatabaseExternalRef')
ORDER BY p.parameter_id;

-- 4. Qué SPs existen y cuándo se tocaron por última vez. modify_date delata si
--    el CREATE OR ALTER llegó a ejecutarse.
SELECT '4. SPs' AS chequeo, name, create_date, modify_date
FROM sys.procedures
WHERE name IN ('sp_SetDatabaseExternalRef', 'sp_GetDatabaseExternalRef',
               'sp_GetDatabasesForSizeSync', 'sp_UpdateDatabaseSize',
               'sp_ReactivateDatabase')
ORDER BY name;
GO
