/* ============================================================================
   Autoservicio de subdominios DNS — catálogo (tablas + 10 SPs)
   ----------------------------------------------------------------------------
   Se ejecuta en la instancia del CATÁLOGO (SQL Server), la misma donde ya viven
   Users y ProvisionedDatabases. En Cloudflare no hay nada que preparar por SQL:
   el backend le habla por su API v4 con el token de Dns:ApiToken.

   ⚠️ ESTE ARCHIVO REEMPLAZA POR COMPLETO a la primera versión del 2026-08-12
   (que asumía la estructura {label}.idempotencia.<zona>). Si esa versión NO se
   ejecutó todavía, ignorarla y correr solo esta. Si SÍ se ejecutó, ver el
   apartado "MIGRAR DESDE LA v1" al final.

   Qué agrega:
     - Tabla DnsRecords        -> un subdominio por fila, con su ciclo de vida.
     - Tabla DnsReservedLabels -> etiquetas que ningún usuario puede pedir.
     - 10 stored procedures: 7 del flujo de usuario + 3 de administración.

   Los subdominios quedan como {label}.idempotencia.coderhivex.com, p. ej.
   airflow.idempotencia.coderhivex.com. El backend arma el nombre de zona y se lo pasa
   a sp_ReserveDnsRecord; el SP compone el FQDN y lo persiste ya compuesto, de
   modo que el catálogo es la única fuente del nombre real.

   Idempotente: las tablas se crean solo si no existen y los SPs usan CREATE OR
   ALTER, así que el script se puede correr varias veces sin efecto adicional.
   ========================================================================== */

USE master;
GO

/* ============================================================================
   1. Tabla DnsRecords
   ----------------------------------------------------------------------------
   Espeja el diseño de ProvisionedDatabases (misma nomenclatura de columnas de
   auditoría, mismo patrón de Status + fechas) para que quien ya conoce ese
   catálogo no tenga que aprender otro modelo.
   ========================================================================== */
IF OBJECT_ID('dbo.DnsRecords', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DnsRecords
    (
        DnsRecordId      INT IDENTITY(1,1) NOT NULL,

        /* Dueño del subdominio. La FK se agrega aparte (paso 3) porque es lo
           único obligatorio de este script que depende del nombre real de la
           tabla de usuarios. */
        UserId           INT           NOT NULL,

        /* Nombre que elige el usuario: el "airflow" de
           airflow.idempotencia.coderhivex.com. 63 es el máximo de una etiqueta DNS
           (RFC 1035). */
        Label            NVARCHAR(63)  NOT NULL,

        /* Célula (equipo de trabajo) bajo la que cuelga el subdominio: el
           "datos" del ejemplo. Hoy es texto validado por formato, sin catálogo
           de células — ver la nota de seguridad en sp_ReserveDnsRecord. */
        Cell             NVARCHAR(63)  NOT NULL,

        /* Nombre completo: {Label}.{Cell}.{zona}. 255 es el máximo de un nombre
           DNS completo. Es el valor que se usa para hablar con Cloudflare, así
           que se guarda tal cual quedó. */
        Fqdn             NVARCHAR(255) NOT NULL,

        RecordType       NVARCHAR(10)  NOT NULL,

        /* Destino. Para los registros tipo A que crea el autoservicio, la IPv4
           pública del servicio del usuario. Se deja NVARCHAR(255) genérico —y
           no un tipo específico de IP— para no cerrar la puerta a CNAME/AAAA. */
        Content          NVARCHAR(255) NOT NULL,

        /* Proxy de Cloudflare. Lo fija la plataforma en 1: de eso depende que
           Total TLS emita el certificado del nombre de dos niveles. */
        Proxied          BIT           NOT NULL CONSTRAINT DF_DnsRecords_Proxied  DEFAULT (1),

        /* TTL en segundos. 1 = "automático", obligatorio cuando hay proxy. */
        Ttl              INT           NOT NULL CONSTRAINT DF_DnsRecords_Ttl      DEFAULT (1),

        /* Id del registro en Cloudflare. NULL mientras la creación no se
           confirme; el backend puede reconciliarlo buscando por Fqdn. */
        ProviderRecordId NVARCHAR(64)  NULL,

        Status           NVARCHAR(20)  NOT NULL CONSTRAINT DF_DnsRecords_Status   DEFAULT ('Provisioning'),

        CreatedAt        DATETIME2(3)  NOT NULL CONSTRAINT DF_DnsRecords_Created  DEFAULT (SYSUTCDATETIME()),
        UpdatedAt        DATETIME2(3)  NOT NULL CONSTRAINT DF_DnsRecords_Updated  DEFAULT (SYSUTCDATETIME()),
        DeletedAt        DATETIME2(3)  NULL,

        /* --- Auditoría de revocación -------------------------------------
           Las tres columnas juntas son el registro de auditoría que pide el
           requisito de control administrativo. Sin ellas, un subdominio
           revocado es indistinguible de uno que el usuario borró, y seis meses
           después nadie puede reconstruir qué pasó ni quién lo decidió. */
        RevokedByUserId  INT           NULL,
        RevokedAt        DATETIME2(3)  NULL,
        RevokeReason     NVARCHAR(500) NULL,

        CONSTRAINT PK_DnsRecords PRIMARY KEY CLUSTERED (DnsRecordId),

        /* Los mismos cinco valores que la clase DnsRecordStatus del backend. Si
           se agrega uno allá hay que agregarlo acá, o el SP falla con un 547 —
           es exactamente el bug 24 del catálogo de bases de datos. */
        CONSTRAINT CK_DnsRecords_Status CHECK
            (Status IN ('Provisioning', 'Active', 'Failed', 'Deleted', 'Revoked')),

        CONSTRAINT CK_DnsRecords_Type   CHECK (RecordType IN ('A', 'AAAA', 'CNAME', 'TXT')),

        /* 1 ("automático") queda fuera del rango 60-86400 que admite Cloudflare,
           así que la condición tiene que ser una disyunción y no un BETWEEN. */
        CONSTRAINT CK_DnsRecords_Ttl    CHECK (Ttl = 1 OR (Ttl >= 60 AND Ttl <= 86400)),

        /* Coherencia del estado revocado: o están las tres columnas de
           auditoría, o no está ninguna. Evita el registro "revocado por nadie,
           sin motivo" que aparece cuando alguien hace un UPDATE a mano. */
        CONSTRAINT CK_DnsRecords_Revoked CHECK
        (
            (Status <> 'Revoked')
            OR (RevokedByUserId IS NOT NULL AND RevokedAt IS NOT NULL AND RevokeReason IS NOT NULL)
        )
    );
END;
GO

/* ----------------------------------------------------------------------------
   Unicidad del nombre — la regla más importante de la tabla.

   Es lo que impide que dos usuarios (o dos células) se queden con el mismo
   subdominio, que es la "colisión de nombres" del requisito.

   Índice FILTRADO en vez de UNIQUE plano: un subdominio eliminado o revocado
   libera el nombre para que cualquiera lo vuelva a pedir, pero la fila se
   conserva para auditoría. Con un UNIQUE plano, el primer usuario que creara y
   borrara "airflow.datos" lo bloquearía para siempre.

   'Failed' también queda fuera del filtro: una reserva que nunca llegó a
   crearse en Cloudflare no está ocupando ningún nombre real.

   Se filtra con IN y no con NOT IN a propósito: IN nombra explícitamente los
   estados que SÍ ocupan el nombre, así que agregar un estado nuevo obliga a
   decidir de qué lado cae en vez de incluirlo por descarte.
   -------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DnsRecords_Fqdn_Alive' AND object_id = OBJECT_ID('dbo.DnsRecords'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_DnsRecords_Fqdn_Alive
        ON dbo.DnsRecords (Fqdn)
        WHERE Status IN ('Provisioning', 'Active');
END;
GO

/* Cubre sp_GetUserDnsRecords y el conteo de cuota de sp_ReserveDnsRecord. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DnsRecords_UserId' AND object_id = OBJECT_ID('dbo.DnsRecords'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DnsRecords_UserId
        ON dbo.DnsRecords (UserId, Status)
        INCLUDE (Label, Cell, Fqdn, RecordType, Content, Proxied, Ttl, CreatedAt, UpdatedAt, DeletedAt);
END;
GO

/* Cubre el listado administrativo filtrado por célula, que es la consulta
   natural de auditoría ("qué tiene levantado el equipo X"). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DnsRecords_Cell' AND object_id = OBJECT_ID('dbo.DnsRecords'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DnsRecords_Cell ON dbo.DnsRecords (Cell, Status);
END;
GO

/* La reconciliación de registros huérfanos busca por Fqdn sin filtrar por
   estado, así que no puede aprovechar el índice filtrado de unicidad. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DnsRecords_Fqdn' AND object_id = OBJECT_ID('dbo.DnsRecords'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DnsRecords_Fqdn ON dbo.DnsRecords (Fqdn);
END;
GO


/* ============================================================================
   2. Tabla DnsReservedLabels — etiquetas prohibidas
   ----------------------------------------------------------------------------
   Se valida contra el Label Y contra la Cell: si alguien crea la célula "www",
   todo lo que cuelgue de ahí compite con la raíz web del dominio.

   Es una TABLA y no una lista dentro del SP para que agregar una etiqueta
   prohibida sea un INSERT y no un redespliegue del backend. El caso real:
   alguien registra "mail" y de golpe el correo del dominio deja de resolver
   como se esperaba.
   ========================================================================== */
IF OBJECT_ID('dbo.DnsReservedLabels', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DnsReservedLabels
    (
        Label  NVARCHAR(63)  NOT NULL,
        Reason NVARCHAR(200) NULL,
        CONSTRAINT PK_DnsReservedLabels PRIMARY KEY CLUSTERED (Label)
    );
END;
GO

/* Semilla. El MERGE evita duplicados si el script se corre de nuevo, y no borra
   las etiquetas que se hayan agregado a mano después. */
MERGE dbo.DnsReservedLabels AS destino
USING (VALUES
    ('www',        'Reservada: raíz web del dominio.'),
    ('api',        'Reservada: API pública de la plataforma.'),
    ('admin',      'Reservada: panel de administración.'),
    ('panel',      'Reservada: panel de administración.'),
    ('dashboard',  'Reservada: panel de administración.'),
    ('mail',       'Reservada: correo del dominio.'),
    ('smtp',       'Reservada: correo del dominio.'),
    ('imap',       'Reservada: correo del dominio.'),
    ('pop',        'Reservada: correo del dominio.'),
    ('mx',         'Reservada: correo del dominio.'),
    ('ftp',        'Reservada: infraestructura.'),
    ('ns1',        'Reservada: servidores de nombres.'),
    ('ns2',        'Reservada: servidores de nombres.'),
    ('cdn',        'Reservada: infraestructura.'),
    ('static',     'Reservada: infraestructura.'),
    ('assets',     'Reservada: infraestructura.'),
    ('coderhivex', 'Reservada: nombre de la organización.'),
    ('colmena',    'Reservada: nombre de la plataforma.'),
    ('test',       'Reservada: ambientes internos.'),
    ('dev',        'Reservada: ambientes internos.'),
    ('staging',    'Reservada: ambientes internos.'),
    ('qa',         'Reservada: ambientes internos.'),
    ('db',         'Reservada: infraestructura de bases de datos.'),
    ('sql',        'Reservada: infraestructura de bases de datos.'),
    ('mysql',      'Reservada: infraestructura de bases de datos.'),
    ('postgres',   'Reservada: infraestructura de bases de datos.'),
    ('mongo',      'Reservada: infraestructura de bases de datos.')
) AS origen (Label, Reason)
    ON destino.Label = origen.Label
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Label, Reason) VALUES (origen.Label, origen.Reason);
GO


/* ============================================================================
   3. Clave foránea hacia la tabla de usuarios
   ----------------------------------------------------------------------------
   Se crea solo si encuentra dbo.Users(UserId); si la tabla de usuarios se llama
   distinto en esta instancia, este bloque no hace nada (y lo avisa) en vez de
   romper todo el script. En ese caso, crear la FK a mano con el nombre correcto.

   Sin ON DELETE CASCADE a propósito: borrar un usuario no debe hacer desaparecer
   en silencio filas cuyos registros siguen existiendo en Cloudflare. Primero se
   eliminan sus subdominios por la API, después el usuario.
   ========================================================================== */
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'UserId')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_DnsRecords_Users')
    BEGIN
        ALTER TABLE dbo.DnsRecords
            ADD CONSTRAINT FK_DnsRecords_Users
                FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId);
    END
END
ELSE
BEGIN
    PRINT 'AVISO: no se encontró dbo.Users(UserId). La tabla DnsRecords queda SIN clave foránea; créala a mano contra la tabla de usuarios real.';
END;
GO


/* ============================================================================
   4. sp_ReserveDnsRecord
   ----------------------------------------------------------------------------
   Es el SP con toda la lógica de negocio del autoservicio: valida el formato de
   la etiqueta y de la célula, las etiquetas reservadas, el formato del destino,
   la cuota por usuario y la colisión de nombres, y recién ahí inserta la fila en
   'Provisioning'.

   Se ejecuta ANTES de llamar a Cloudflare a propósito: un usuario que excedió su
   cuota o pidió una etiqueta inválida no llega siquiera a gastar una llamada a
   la API externa, que tiene cuota compartida por toda la plataforma.

   ⚠️ NOTA DE SEGURIDAD CONOCIDA: hoy NO se valida que el usuario pertenezca a la
   célula que dice, porque no existe todavía un catálogo de células ni una
   relación usuario↔célula en la base. Cualquier usuario autenticado puede crear
   un subdominio bajo el nombre de cualquier célula. Es una decisión consciente
   para no bloquear esta entrega, y el control mientras tanto es a posteriori
   (sp_GetAllDnsRecords + sp_RevokeDnsRecord, que filtran por célula justamente
   para esto). Cuando exista el catálogo, la validación de pertenencia va acá y
   nada del backend cambia.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_ReserveDnsRecord
    @UserId     INT,
    @Label      NVARCHAR(63),
    @Cell       NVARCHAR(63),
    @ZoneName   NVARCHAR(127),
    @RecordType NVARCHAR(10),
    @Content    NVARCHAR(255),
    @Proxied    BIT,
    @Ttl        INT
AS
BEGIN
    SET NOCOUNT ON;

    /* Cuota de subdominios por usuario — el "limitar la cantidad de subdominios
       que un usuario puede crear" del requisito.

       Constante acá (y no columna en Users) porque hoy es la misma para todos;
       si mañana hay planes distintos, esto se convierte en una lectura de Users
       y el resto del SP no cambia. Cambiar el número es un CREATE OR ALTER de
       este SP: no requiere migración de datos ni tocar el backend.

       No hay ninguna validación de cuota en el backend a propósito. Si
       estuviera en los dos lados, tarde o temprano dirían cosas distintas y
       ganaría la más restrictiva sin que nadie entienda por qué. */
    DECLARE @MaxRecordsPerUser INT = 3;

    /* Normalización defensiva: el DTO del backend ya baja a minúsculas y hace
       trim, pero este SP también se puede invocar desde SSMS y no debe depender
       de que el llamador se haya portado bien. */
    SET @Label    = LOWER(LTRIM(RTRIM(@Label)));
    SET @Cell     = LOWER(LTRIM(RTRIM(@Cell)));
    SET @ZoneName = LOWER(LTRIM(RTRIM(@ZoneName)));
    SET @Content  = LTRIM(RTRIM(@Content));

    IF @ZoneName IS NULL OR LEN(@ZoneName) = 0
        THROW 50030, 'No se recibió el dominio de zona para armar el subdominio.', 1;

    /* --- Formato de la etiqueta y de la célula (RFC 1035, juego LDH) -------
       Reglas separadas para poder dar un mensaje concreto en vez de un
       "nombre inválido" genérico. El rango [^a-z0-9-] funciona sobre el texto ya
       pasado a minúsculas; con una intercalación case-insensitive (la de por
       defecto) también aceptaría mayúsculas, y por eso el LOWER de arriba no es
       cosmético. */
    IF @Label IS NULL OR LEN(@Label) < 3 OR LEN(@Label) > 63
        THROW 50031, 'El nombre del subdominio debe tener entre 3 y 63 caracteres.', 1;

    IF @Label LIKE '%[^a-z0-9-]%' OR LEFT(@Label, 1) = '-' OR RIGHT(@Label, 1) = '-'
        THROW 50032, 'El nombre solo puede contener letras, números y guiones, y no puede empezar ni terminar con guion.', 1;

    IF @Cell IS NULL OR LEN(@Cell) < 3 OR LEN(@Cell) > 63
        THROW 50033, 'La célula debe tener entre 3 y 63 caracteres.', 1;

    IF @Cell LIKE '%[^a-z0-9-]%' OR LEFT(@Cell, 1) = '-' OR RIGHT(@Cell, 1) = '-'
        THROW 50034, 'La célula solo puede contener letras, números y guiones, y no puede empezar ni terminar con guion.', 1;

    /* Se valida contra las dos partes: una célula llamada "www" o "mail"
       comprometería el dominio igual que una etiqueta con ese nombre. */
    IF EXISTS (SELECT 1 FROM dbo.DnsReservedLabels WHERE Label IN (@Label, @Cell))
        THROW 50035, 'Ese nombre o esa célula están reservados por la plataforma. Elige otro.', 1;

    /* --- Destino -----------------------------------------------------------
       Para los registros A se comprueba la forma de una IPv4 en decimal
       punteado. Es una validación de RESPALDO: la buena —que además descarta
       rangos privados, loopback y CGNAT, inalcanzables para el proxy de
       Cloudflare— vive en el backend (DTOs/IpAddressRules.cs), donde se puede
       parsear de verdad. Acá se atrapa el caso de una invocación directa al SP
       que se saltee esa capa. */
    IF @RecordType = 'A'
    BEGIN
        IF @Content NOT LIKE '[0-9]%.[0-9]%.[0-9]%.[0-9]%'
           OR @Content LIKE '%[^0-9.]%'
           OR LEN(@Content) - LEN(REPLACE(@Content, '.', '')) <> 3
            THROW 50036, 'El destino de un registro A debe ser una dirección IPv4.', 1;
    END

    /* --- Cuota ------------------------------------------------------------
       Cuenta solo los estados vivos: un subdominio eliminado, revocado o fallido
       no debe seguir consumiendo el cupo del usuario. */
    DECLARE @Actuales INT = (
        SELECT COUNT(*)
        FROM dbo.DnsRecords
        WHERE UserId = @UserId
          AND Status IN ('Provisioning', 'Active')
    );

    IF @Actuales >= @MaxRecordsPerUser
        THROW 50037, 'Alcanzaste el máximo de subdominios permitidos. Elimina uno antes de crear otro.', 1;

    DECLARE @Fqdn NVARCHAR(255) = @Label + '.' + @Cell + '.' + @ZoneName;

    IF LEN(@Fqdn) > 255
        THROW 50038, 'El nombre completo del subdominio supera los 255 caracteres.', 1;

    /* --- Colisión ---------------------------------------------------------
       El índice UX_DnsRecords_Fqdn_Alive ya lo garantiza; esta comprobación
       previa existe para devolver un mensaje entendible en vez de un error de
       violación de índice único (2601), que el middleware traduciría a un 500
       genérico. La condición de carrera que queda —dos peticiones simultáneas
       con el mismo nombre— la resuelve el índice, y el segundo INSERT falla: es
       el orden correcto, porque el índice es la garantía dura y esto solo es
       cortesía. */
    IF EXISTS (
        SELECT 1 FROM dbo.DnsRecords
        WHERE Fqdn = @Fqdn AND Status IN ('Provisioning', 'Active')
    )
        THROW 50039, 'Ese subdominio ya está en uso. Elige otro nombre.', 1;

    /* --- Inserción --------------------------------------------------------- */
    INSERT INTO dbo.DnsRecords
        (UserId, Label, Cell, Fqdn, RecordType, Content, Proxied, Ttl, Status)
    VALUES
        (@UserId, @Label, @Cell, @Fqdn, @RecordType, @Content, @Proxied, @Ttl, 'Provisioning');

    DECLARE @DnsRecordId INT = CAST(SCOPE_IDENTITY() AS INT);

    /* Estas columnas, con estos nombres, son las que EF Core mapea sobre el tipo
       DnsRecordReservation del backend. Cambiar un nombre acá rompe el mapeo en
       silencio (la propiedad queda en su valor por defecto), así que conviene
       tocarlas junto con el modelo. */
    SELECT  DnsRecordId, Label, Cell, Fqdn, RecordType, Content, Proxied, Ttl, CreatedAt
    FROM dbo.DnsRecords
    WHERE DnsRecordId = @DnsRecordId;
END;
GO


/* ============================================================================
   5. sp_ConfirmDnsRecord
   ----------------------------------------------------------------------------
   Cierra el flujo de creación: el registro ya existe en Cloudflare y acá se
   guarda el id que asignó el proveedor, que es lo único que permite
   actualizarlo, borrarlo o revocarlo después.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_ConfirmDnsRecord
    @DnsRecordId      INT,
    @ProviderRecordId NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.DnsRecords
        WHERE DnsRecordId = @DnsRecordId AND Status = 'Provisioning'
    )
        THROW 50040, 'La reserva de subdominio no existe o ya no está pendiente de confirmar.', 1;

    UPDATE dbo.DnsRecords
    SET Status           = 'Active',
        ProviderRecordId = @ProviderRecordId,
        UpdatedAt        = SYSUTCDATETIME()
    WHERE DnsRecordId = @DnsRecordId;
END;
GO


/* ============================================================================
   6. sp_FailDnsRecord
   ----------------------------------------------------------------------------
   Revierte una reserva cuya creación en Cloudflare falló. La fila NO se borra:
   se conserva para poder diagnosticar qué se intentó y cuándo. Como 'Failed'
   queda fuera del índice filtrado de unicidad, el nombre vuelve a estar libre de
   inmediato — que es lo que le importa al usuario que quiere reintentar.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_FailDnsRecord
    @DnsRecordId INT
AS
BEGIN
    SET NOCOUNT ON;

    /* Sin guarda de estado ni THROW: lo llama el bloque de reversión del
       backend, que ya está manejando otra excepción. Si acá lanzara, taparía la
       causa original del fallo y el diagnóstico se volvería mucho más difícil. */
    UPDATE dbo.DnsRecords
    SET Status    = 'Failed',
        UpdatedAt = SYSUTCDATETIME()
    WHERE DnsRecordId = @DnsRecordId
      AND Status = 'Provisioning';
END;
GO


/* ============================================================================
   7. sp_GetUserDnsRecords  —  GET /dns
   ----------------------------------------------------------------------------
   No devuelve ProviderRecordId: es un detalle interno de Cloudflare que no le
   sirve al frontend.

   Excluye los estados terminales: el usuario ve los subdominios que tiene, no el
   historial de los que tuvo. Las filas siguen ahí para auditoría, y el listado
   administrativo sí las alcanza.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_GetUserDnsRecords
    @UserId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  DnsRecordId, UserId, Label, Cell, Fqdn, RecordType, Content,
            Proxied, Ttl, Status, CreatedAt, UpdatedAt, DeletedAt
    FROM dbo.DnsRecords
    WHERE UserId = @UserId
      AND Status IN ('Provisioning', 'Active')
    ORDER BY CreatedAt DESC;
END;
GO


/* ============================================================================
   8. sp_GetDnsRecordDetail  —  GET /dns/{id} y uso interno
   ----------------------------------------------------------------------------
   Incluye ProviderRecordId, que el backend necesita para actualizar o borrar en
   Cloudflare.

   Filtra por (@DnsRecordId, @UserId) juntos: si el registro es de otro usuario
   no devuelve fila, y el backend lo traduce al mismo 404 que "no existe". Es
   deliberado — así nadie puede averiguar qué identificadores ajenos existen.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_GetDnsRecordDetail
    @DnsRecordId INT,
    @UserId      INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  DnsRecordId, UserId, Label, Cell, Fqdn, RecordType, Content,
            Proxied, Ttl, ProviderRecordId, Status, CreatedAt, UpdatedAt, DeletedAt
    FROM dbo.DnsRecords
    WHERE DnsRecordId = @DnsRecordId
      AND UserId      = @UserId
      AND Status NOT IN ('Deleted', 'Revoked');
END;
GO


/* ============================================================================
   9. sp_UpdateDnsRecord  —  PUT /dns/{id}
   ----------------------------------------------------------------------------
   Persiste la nueva IP de destino DESPUÉS de que el cambio ya se aplicó en
   Cloudflare.

   NO recibe Proxied ni Ttl: los fija la plataforma y de ellos depende el
   certificado. Aceptarlos acá sería ofrecer, desde la capa de datos, una forma
   de dejar un subdominio sin HTTPS.

   El Fqdn tampoco se puede cambiar: eso sería otro subdominio. Se borra y se
   crea, y así el catálogo conserva la historia de ambos.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_UpdateDnsRecord
    @DnsRecordId INT,
    @UserId      INT,
    @Content     NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.DnsRecords
        WHERE DnsRecordId = @DnsRecordId AND UserId = @UserId AND Status = 'Active'
    )
        THROW 50041, 'El subdominio no existe, no te pertenece, o no está activo.', 1;

    UPDATE dbo.DnsRecords
    SET Content   = LTRIM(RTRIM(@Content)),
        UpdatedAt = SYSUTCDATETIME()
    WHERE DnsRecordId = @DnsRecordId
      AND UserId      = @UserId;
END;
GO


/* ============================================================================
   10. sp_DeleteDnsRecord  —  DELETE /dns/{id}
   ----------------------------------------------------------------------------
   Borrado LÓGICO iniciado por el USUARIO. Se llama después de que el registro ya
   se eliminó en Cloudflare. La fila se conserva para auditoría, pero al salir de
   los estados vivos deja de ocupar el nombre en el índice filtrado, así que el
   subdominio queda libre para volver a pedirse — incluso por otro usuario.

   ProviderRecordId se pone en NULL: apuntaba a un registro que ya no existe, y
   dejarlo invitaría a que alguien intentara operar sobre él.

   Acepta 'Provisioning' y 'Failed' además de 'Active' para poder limpiar una
   reserva que quedó a medias (creada en Cloudflare pero nunca confirmada acá).
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_DeleteDnsRecord
    @DnsRecordId INT,
    @UserId      INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.DnsRecords
        WHERE DnsRecordId = @DnsRecordId
          AND UserId      = @UserId
          AND Status IN ('Provisioning', 'Active', 'Failed')
    )
        THROW 50042, 'El subdominio no existe, no te pertenece, o ya fue eliminado.', 1;

    UPDATE dbo.DnsRecords
    SET Status           = 'Deleted',
        DeletedAt        = SYSUTCDATETIME(),
        UpdatedAt        = SYSUTCDATETIME(),
        ProviderRecordId = NULL
    WHERE DnsRecordId = @DnsRecordId
      AND UserId      = @UserId;
END;
GO


/* ============================================================================
   11. sp_GetAllDnsRecords  —  GET /admin/dns   (rol Admin)
   ----------------------------------------------------------------------------
   Listado de auditoría: TODOS los registros de TODOS los usuarios, con filtros
   opcionales. Es la pieza de "el equipo debe poder auditar y listar" del
   requisito.

   Todos los filtros son opcionales y se expresan con el patrón
   "@Param IS NULL OR columna = @Param": una sola consulta que sirve para
   cualquier combinación, en vez de SQL dinámico (que abriría una superficie de
   inyección donde hoy no la hay).

   @MinDaysSinceUpdate es el filtro que sostiene la revocación por inactividad:
   con 90 salen los candidatos a revocar. El cálculo se hace acá y no en el
   backend para que el valor mostrado y el filtro usen el mismo reloj — el del
   servidor de base de datos.

   Sin @Status devuelve solo los vivos: el inventario histórico completo, con
   todo lo borrado y revocado, es la excepción y hay que pedirlo explícitamente.

   El correo del dueño sale de un JOIN con la tabla de usuarios. Como ese es el
   segundo punto del script que depende del esquema existente, se generan dos
   variantes del SP según lo que haya realmente en la base — así el despliegue no
   se rompe si la columna se llama distinto.
   ========================================================================== */
DECLARE @tieneEmail BIT =
    CASE WHEN OBJECT_ID('dbo.Users', 'U') IS NOT NULL
              AND EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'Email')
         THEN 1 ELSE 0 END;

DECLARE @sql NVARCHAR(MAX) = N'
CREATE OR ALTER PROCEDURE sp_GetAllDnsRecords
    @Cell               NVARCHAR(63)  = NULL,
    @UserId             INT           = NULL,
    @Status             NVARCHAR(20)  = NULL,
    @MinDaysSinceUpdate INT           = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  d.DnsRecordId,
            d.UserId,
            ' + CASE WHEN @tieneEmail = 1
                     THEN N'ISNULL(u.Email, '''')'
                     ELSE N'CAST('''' AS NVARCHAR(150))' END + N' AS UserEmail,
            d.Label, d.Cell, d.Fqdn, d.RecordType, d.Content,
            d.Proxied, d.Ttl, d.Status, d.CreatedAt, d.UpdatedAt, d.DeletedAt,
            DATEDIFF(DAY, d.UpdatedAt, SYSUTCDATETIME()) AS DaysSinceUpdate
    FROM dbo.DnsRecords d
    ' + CASE WHEN @tieneEmail = 1
             THEN N'LEFT JOIN dbo.Users u ON u.UserId = d.UserId'
             ELSE N'' END + N'
    WHERE (@Cell   IS NULL OR d.Cell   = @Cell)
      AND (@UserId IS NULL OR d.UserId = @UserId)
      AND (
            (@Status IS NULL AND d.Status IN (''Provisioning'', ''Active''))
            OR (@Status IS NOT NULL AND d.Status = @Status)
          )
      AND (@MinDaysSinceUpdate IS NULL
           OR DATEDIFF(DAY, d.UpdatedAt, SYSUTCDATETIME()) >= @MinDaysSinceUpdate)
    ORDER BY d.UpdatedAt ASC;   -- lo más viejo primero: es lo que se audita
END;';

EXEC sp_executesql @sql;

IF @tieneEmail = 0
    PRINT 'AVISO: no se encontró dbo.Users(Email). sp_GetAllDnsRecords devuelve UserEmail vacío; ajustar el JOIN a la columna real si se quiere el correo en el listado.';
GO


/* ============================================================================
   12. sp_GetDnsRecordDetailAdmin  —  GET /admin/dns/{id}   (rol Admin)
   ----------------------------------------------------------------------------
   Igual que sp_GetDnsRecordDetail pero SIN filtro de propiedad y SIN excluir los
   estados terminales: auditar es justamente poder mirar lo que ya no está vivo.

   Es un SP aparte y no un parámetro opcional del otro a propósito. Un
   @UserId = NULL que significara "no filtres" convertiría un olvido en una fuga
   de datos de todos los usuarios; con dos SPs, el que no filtra solo se puede
   invocar desde el repositorio administrativo.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_GetDnsRecordDetailAdmin
    @DnsRecordId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  DnsRecordId, UserId, Label, Cell, Fqdn, RecordType, Content,
            Proxied, Ttl, ProviderRecordId, Status, CreatedAt, UpdatedAt, DeletedAt
    FROM dbo.DnsRecords
    WHERE DnsRecordId = @DnsRecordId;
END;
GO


/* ============================================================================
   13. sp_RevokeDnsRecord  —  POST /admin/dns/{id}/revoke   (rol Admin)
   ----------------------------------------------------------------------------
   Revocación por parte del equipo (inactividad, abuso). Se llama después de
   eliminar el registro en Cloudflare.

   Estado 'Revoked' y no 'Deleted': los dos liberan el nombre, pero solo así una
   auditoría puede distinguir lo que el usuario dio de baja de lo que el equipo
   le quitó. Junto con RevokedByUserId/RevokedAt/RevokeReason, deja el rastro
   completo de la decisión.
   ========================================================================== */
CREATE OR ALTER PROCEDURE sp_RevokeDnsRecord
    @DnsRecordId     INT,
    @RevokedByUserId INT,
    @Reason          NVARCHAR(500)
AS
BEGIN
    SET NOCOUNT ON;

    IF @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason))) = 0
        THROW 50043, 'La revocación requiere un motivo.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.DnsRecords
        WHERE DnsRecordId = @DnsRecordId
          AND Status IN ('Provisioning', 'Active', 'Failed')
    )
        THROW 50044, 'El subdominio no existe o ya no está activo.', 1;

    UPDATE dbo.DnsRecords
    SET Status           = 'Revoked',
        RevokedByUserId  = @RevokedByUserId,
        RevokedAt        = SYSUTCDATETIME(),
        RevokeReason     = LTRIM(RTRIM(@Reason)),
        UpdatedAt        = SYSUTCDATETIME(),
        ProviderRecordId = NULL
    WHERE DnsRecordId = @DnsRecordId;
END;
GO


/* ============================================================================
   VERIFICACIÓN
   ----------------------------------------------------------------------------
   1) Los 10 SPs existen:

        SELECT name FROM sys.procedures
        WHERE name LIKE 'sp_%Dns%' ORDER BY name;
        -- Esperado: sp_ConfirmDnsRecord, sp_DeleteDnsRecord, sp_FailDnsRecord,
        --           sp_GetAllDnsRecords, sp_GetDnsRecordDetail,
        --           sp_GetDnsRecordDetailAdmin, sp_GetUserDnsRecords,
        --           sp_ReserveDnsRecord, sp_RevokeDnsRecord, sp_UpdateDnsRecord

   2) La clave foránea quedó creada:

        SELECT name FROM sys.foreign_keys WHERE name = 'FK_DnsRecords_Users';

   3) Flujo completo en seco (NO toca Cloudflare). Reemplazar <userId>:

        EXEC sp_ReserveDnsRecord
             @UserId = <userId>, @Label = 'airflow', @Cell = 'idempotencia',
             @ZoneName = 'coderhivex.com', @RecordType = 'A',
             @Content = '203.0.113.10', @Proxied = 1, @Ttl = 1;
        -- anotar el DnsRecordId devuelto

        EXEC sp_ConfirmDnsRecord   @DnsRecordId = <id>, @ProviderRecordId = 'prueba-local';
        EXEC sp_GetUserDnsRecords  @UserId = <userId>;
        EXEC sp_GetDnsRecordDetail @DnsRecordId = <id>, @UserId = <userId>;
        EXEC sp_UpdateDnsRecord    @DnsRecordId = <id>, @UserId = <userId>, @Content = '198.51.100.5';
        EXEC sp_DeleteDnsRecord    @DnsRecordId = <id>, @UserId = <userId>;

      (203.0.113.0/24 y 198.51.100.0/24 son rangos de documentación: sirven para
      la prueba del catálogo, pero el backend los RECHAZA como destino real —
      ver DTOs/IpAddressRules.cs.)

   4) Administración:

        EXEC sp_GetAllDnsRecords;                              -- inventario vivo
        EXEC sp_GetAllDnsRecords @Cell = 'idempotencia';              -- por célula
        EXEC sp_GetAllDnsRecords @MinDaysSinceUpdate = 90;     -- candidatos a revocar
        EXEC sp_GetAllDnsRecords @Status = 'Revoked';          -- histórico de revocados
        EXEC sp_GetDnsRecordDetailAdmin @DnsRecordId = <id>;
        EXEC sp_RevokeDnsRecord @DnsRecordId = <id>, @RevokedByUserId = <adminId>,
             @Reason = 'Inactivo por más de 90 días.';

   5) Las validaciones rechazan lo que deben (cada una debe lanzar):

        EXEC sp_ReserveDnsRecord <userId>, 'ab',   'idempotencia', 'coderhivex.com', 'A', '203.0.113.10', 1, 1; -- 50031
        EXEC sp_ReserveDnsRecord <userId>, 'a_b',  'idempotencia', 'coderhivex.com', 'A', '203.0.113.10', 1, 1; -- 50032
        EXEC sp_ReserveDnsRecord <userId>, 'app',  'ab',    'coderhivex.com', 'A', '203.0.113.10', 1, 1; -- 50033
        EXEC sp_ReserveDnsRecord <userId>, 'www',  'idempotencia', 'coderhivex.com', 'A', '203.0.113.10', 1, 1; -- 50035
        EXEC sp_ReserveDnsRecord <userId>, 'app',  'idempotencia', 'coderhivex.com', 'A', 'no-es-una-ip', 1, 1; -- 50036

   ----------------------------------------------------------------------------
   MIGRAR DESDE LA v1 (solo si se llegó a ejecutar la primera versión del
   2026-08-12, la de {label}.idempotencia.<zona>)
   ----------------------------------------------------------------------------
   La tabla de la v1 no tiene Cell ni las columnas de revocación, y este script
   no altera una tabla que ya existe. Como en la v1 no había registros reales
   creados todavía, lo más limpio es tirarla y volver a correr este archivo:

        -- Verificar primero que no haya nada que perder:
        SELECT COUNT(*) FROM dbo.DnsRecords WHERE Status IN ('Provisioning','Active');
        -- Si devuelve 0:
        DROP TABLE dbo.DnsRecords;
        -- y volver a ejecutar este script completo.

   Si devuelve algo distinto de 0, esos registros existen en Cloudflare: hay que
   borrarlos por el panel o la API ANTES de tirar la tabla, o quedan resolviendo
   sin nadie que los administre.

   ----------------------------------------------------------------------------
   REVERTIR (entorno de pruebas solamente):

        DROP PROCEDURE IF EXISTS sp_ReserveDnsRecord, sp_ConfirmDnsRecord,
             sp_FailDnsRecord, sp_GetUserDnsRecords, sp_GetDnsRecordDetail,
             sp_UpdateDnsRecord, sp_DeleteDnsRecord, sp_GetAllDnsRecords,
             sp_GetDnsRecordDetailAdmin, sp_RevokeDnsRecord;
        DROP TABLE IF EXISTS dbo.DnsRecords;
        DROP TABLE IF EXISTS dbo.DnsReservedLabels;

   Los registros que ya existan en Cloudflare NO se borran con esto.
   ========================================================================== */
