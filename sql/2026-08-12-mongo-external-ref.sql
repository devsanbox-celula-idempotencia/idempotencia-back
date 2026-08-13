/* ============================================================================
   Referencia externa de aprovisionamiento — migración de MongoDB a la API del
   equipo (https://mongo.szapatar.dev)
   ----------------------------------------------------------------------------
   Hasta ahora el catálogo asumía que el backend creaba la base con el nombre
   que él mismo había reservado (sp_ReserveDatabase) y que podía volver a
   operarla por ese nombre. Con la API externa de MongoDB eso deja de ser
   cierto en dos puntos:

     1. La API genera un nombre físico ALEATORIO, independiente del que se le
        pide. ProvisionedDatabases.DbName sigue siendo el nombre lógico del
        catálogo, pero ya no es el nombre al que el usuario se conecta.
     2. La API expone sus recursos por un `id` propio, no por nombre. Sin
        guardar ese id, una base creada por la API queda imposible de eliminar
        o de rotarle credenciales desde Colmena.

   Este script agrega las dos columnas que faltaban y los dos SPs para
   escribirlas y leerlas. NO toca ningún SP existente: sp_ReserveDatabase,
   sp_ConfirmDatabase, sp_GetDatabaseDetail y compañía siguen exactamente
   iguales, y las bases de los otros tres motores siguen con las dos columnas
   en NULL, que es justo lo que significa "esta base la administra el backend
   directamente".

   Ejecutar en la instancia del catálogo. Es idempotente: se puede correr
   varias veces sin efecto adicional.
   ========================================================================== */

USE master;
GO

/* ----------------------------------------------------------------------------
   1. Columnas nuevas en ProvisionedDatabases

   Ambas NULLABLE y sin default a propósito: NULL no es "falta el dato", es la
   afirmación "esta base no la administra ningún servicio externo". Es el estado
   correcto para las bases de SqlServer/Postgres/MySQL y para las de Mongo
   creadas ANTES de esta migración, que siguen viviendo en el servidor propio.

   ExternalId es NVARCHAR(100) y no un tipo más estrecho porque el formato del
   id lo decide un servicio ajeno y puede cambiar sin avisarnos; ExternalDbName
   usa el mismo ancho que DbName (128) para no ser el eslabón que trunque.
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

/* ----------------------------------------------------------------------------
   2. sp_SetDatabaseExternalRef
   Guarda la referencia que devolvió el servicio externo al crear la base.

   NO recibe @UserId: lo llama el backend inmediatamente después de que la
   creación física confirmó, dentro del mismo flujo que ya validó la propiedad
   al reservar. Es el mismo criterio de sp_UpdateDatabaseSize, que tampoco lo
   pide por ser una escritura del sistema y no una acción del usuario.

   Guarda contra 'Deleted' para no revivir con datos una fila ya borrada.

   El THROW cuando no se afecta ninguna fila es deliberado: si esta escritura se
   pierde en silencio, la base queda huérfana en la API externa (existe, cobra
   recursos, y Colmena no sabe cómo referirse a ella). Es preferible fallar y
   que el orquestador revierta la creación.
   -------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE sp_SetDatabaseExternalRef
    @DatabaseId     INT,
    @ExternalId     NVARCHAR(100),
    @ExternalDbName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    IF @ExternalId IS NULL OR LTRIM(RTRIM(@ExternalId)) = ''
    BEGIN
        THROW 50030, 'El identificador externo no puede ser vacío.', 1;
    END

    UPDATE ProvisionedDatabases
    SET ExternalId     = @ExternalId,
        ExternalDbName = @ExternalDbName
    WHERE DatabaseId = @DatabaseId
      AND Status <> 'Deleted';

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 50031, 'No se encontró una base de datos activa con ese identificador para guardar su referencia externa.', 1;
    END
END;
GO

/* ----------------------------------------------------------------------------
   3. sp_GetDatabaseExternalRef
   Lee la referencia externa de una base del usuario.

   Va en un SP aparte en vez de agregarle dos columnas a sp_GetDatabaseDetail
   por dos razones: ese SP ya lo consumen cuatro flujos y su forma está acoplada
   al tipo C# ProvisionedDatabaseDetail, y esta referencia solo hace falta en el
   ciclo de vida (eliminar, desactivar, reactivar, resetear contraseña), no en
   la lectura de detalle. El costo es una llamada extra al catálogo en
   operaciones que ya hacen al menos una llamada HTTP externa mucho más cara.

   SÍ recibe @UserId, a diferencia de sp_SetDatabaseExternalRef: este se llama
   en nombre de un usuario autenticado, y el mismo criterio de los demás SPs de
   lectura aplica — no devolver nada de una base ajena.

   NO filtra por Status: la referencia se necesita justamente para eliminar una
   base ya 'Inactive', y también sirve para auditar una 'Deleted'.
   -------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE sp_GetDatabaseExternalRef
    @DatabaseId INT,
    @UserId     INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  pd.ExternalId,
            pd.ExternalDbName
    FROM ProvisionedDatabases pd
    WHERE pd.DatabaseId = @DatabaseId
      AND pd.UserId     = @UserId;
END;
GO
