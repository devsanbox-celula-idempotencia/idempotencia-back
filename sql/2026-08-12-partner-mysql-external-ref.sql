/* ============================================================================
   Referencia externa, parte 2 — migración de MySQL a la API de la célula socia
   (https://api.aba.andrescortes.dev)
   ----------------------------------------------------------------------------
   Continúa lo que empezó `2026-08-12-mongo-external-ref.sql`. Ese script cubría
   lo que hacía falta para MongoDB: guardar el id externo y el nombre físico que
   genera el servicio ajeno. La API de MySQL de la célula socia obliga a guardar
   dos cosas más:

     1. El USUARIO. Su `POST /partners/databases` no lleva cuerpo: ni el nombre
        de la base ni el del usuario se pueden proponer, los genera ella con su
        prefijo (`alpha_7f3a9c1e`). El `LoginName` del catálogo deja de ser el
        usuario real con el que se conecta el estudiante.
     2. La CUOTA. El límite de almacenamiento lo fija el socio (hoy 20 MB) y es
        el que realmente pausa la base al superarse. Reportar el `MaxStorageMB`
        del catálogo cuando difiere le muestra al usuario un límite falso y su
        base se bloquea sin explicación.

   Este script es AUTOSUFICIENTE: crea las cuatro columnas (las dos del script
   de Mongo incluidas) y redefine los dos SPs en su forma final. Se puede correr
   antes, después o en vez del script de Mongo — el resultado es el mismo. Los
   dos parámetros nuevos de `sp_SetDatabaseExternalRef` tienen default NULL, así
   que una versión vieja del backend que solo mande tres sigue funcionando.

   Ejecutar en la instancia del catálogo. Es idempotente: se puede correr varias
   veces sin efecto adicional.
   ========================================================================== */

USE master;
GO

/* ----------------------------------------------------------------------------
   1. Columnas en ProvisionedDatabases

   Las cuatro NULLABLE y sin default a propósito: NULL no es "falta el dato", es
   la afirmación "esto lo gobierna el backend, no un servicio externo". Es el
   estado correcto para SqlServer/Postgres y para cualquier base creada antes de
   estas migraciones.

   ExternalLoginName usa el mismo ancho que LoginName (128) para no ser el
   eslabón que trunque; ExternalId es NVARCHAR(100) porque su formato lo decide
   un servicio ajeno y puede cambiar sin avisarnos (la API de MySQL usa un
   entero, la de Mongo una cadena — caben las dos).
   -------------------------------------------------------------------------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalId')
BEGIN
    ALTER TABLE ProvisionedDatabases ADD ExternalId NVARCHAR(100) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalDbName')
BEGIN
    ALTER TABLE ProvisionedDatabases ADD ExternalDbName NVARCHAR(128) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalLoginName')
BEGIN
    ALTER TABLE ProvisionedDatabases ADD ExternalLoginName NVARCHAR(128) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.ProvisionedDatabases') AND name = 'ExternalMaxStorageMB')
BEGIN
    ALTER TABLE ProvisionedDatabases ADD ExternalMaxStorageMB INT NULL;
END
GO

/* ----------------------------------------------------------------------------
   2. sp_SetDatabaseExternalRef (forma final, 5 parámetros)
   Guarda la referencia que devolvió el servicio externo al crear la base.

   NO recibe @UserId: lo llama el backend inmediatamente después de que la
   creación física confirmó, dentro del mismo flujo que ya validó la propiedad
   al reservar. Mismo criterio de sp_UpdateDatabaseSize, que tampoco lo pide por
   ser una escritura del sistema y no una acción del usuario.

   Guarda contra 'Deleted' para no revivir con datos una fila ya borrada.

   El THROW cuando no se afecta ninguna fila es deliberado: si esta escritura se
   pierde en silencio, la base queda huérfana en el servicio externo (existe,
   consume la cuota de la célula, y el catálogo no sabe cómo referirse a ella).
   Es preferible fallar y que el orquestador revierta la creación.

   Los dos parámetros nuevos van con DEFAULT NULL para que este SP siga
   sirviendo a un backend que todavía mande solo tres (el de la migración de
   Mongo). No se "conserva lo que hubiera" cuando llegan en NULL: una referencia
   externa se escribe una sola vez, al crear, y sobrescribir con NULL lo que ya
   era NULL es un no-op.
   -------------------------------------------------------------------------- */
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
    BEGIN
        THROW 50030, 'El identificador externo no puede ser vacío.', 1;
    END

    IF @ExternalMaxStorageMB IS NOT NULL AND @ExternalMaxStorageMB <= 0
    BEGIN
        THROW 50032, 'La cuota externa de almacenamiento debe ser mayor que cero.', 1;
    END

    UPDATE ProvisionedDatabases
    SET ExternalId           = @ExternalId,
        ExternalDbName       = @ExternalDbName,
        ExternalLoginName    = @ExternalLoginName,
        ExternalMaxStorageMB = @ExternalMaxStorageMB
    WHERE DatabaseId = @DatabaseId
      AND Status <> 'Deleted';

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 50031, 'No se encontró una base de datos activa con ese identificador para guardar su referencia externa.', 1;
    END
END;
GO

/* ----------------------------------------------------------------------------
   3. sp_GetDatabaseExternalRef (forma final, 4 columnas)
   Lee la referencia externa de una base del usuario.

   Va en un SP aparte en vez de agregarle columnas a sp_GetDatabaseDetail por
   dos razones: ese SP ya lo consumen varios flujos y su forma está acoplada al
   tipo C# ProvisionedDatabaseDetail, y esta referencia solo hace falta en el
   ciclo de vida (eliminar, desactivar, reactivar, resetear contraseña) y en el
   detalle. El costo es una llamada extra al catálogo en operaciones que ya
   hacen al menos una llamada HTTP externa mucho más cara.

   SÍ recibe @UserId, a diferencia de sp_SetDatabaseExternalRef: este se llama
   en nombre de un usuario autenticado, y el mismo criterio de los demás SPs de
   lectura aplica — no devolver nada de una base ajena.

   NO filtra por Status: la referencia se necesita justamente para eliminar una
   base ya 'Inactive', y también sirve para auditar una 'Deleted'.

   El orden de las columnas importa: EF Core mapea el resultado sobre
   ExternalDatabaseRef por nombre, pero mantener el orden estable evita sorpresas
   si algún día se consume por posición.
   -------------------------------------------------------------------------- */
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
