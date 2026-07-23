using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>Datos de entrada para aprovisionar una nueva base de datos.</summary>
public class CreateDatabaseRequest
{
    private string _engine = string.Empty;
    private string _dbName = string.Empty;

    /// <summary>
    /// Motor solicitado: "SqlServer", "Postgres", "MySql" o "Mongo"
    /// (ver <see cref="idempotencia.Models.DatabaseEngine"/>). El
    /// <see cref="RegularExpressionAttribute"/> rechaza cualquier otro valor
    /// con un 400 de validación estándar, antes de que llegue al factory de
    /// provisioners (que igual lo valida de nuevo — defensa en profundidad).
    /// </summary>
    [Required(ErrorMessage = "El motor es obligatorio.")]
    [MaxLength(20)]
    [RegularExpression("^(SqlServer|Postgres|MySql|Mongo)$",
        ErrorMessage = "Motor no soportado. Debe ser SqlServer, Postgres, MySql o Mongo.")]
    public string Engine
    {
        get => _engine;
        set => _engine = InputNormalization.TrimOrEmpty(value);
    }

    /// <summary>
    /// Nombre lógico de la BD elegido por el usuario (el backend le antepone
    /// un prefijo por usuario antes de crearla físicamente). Restringido a
    /// letras/dígitos/guion bajo, empezando por una letra: es exactamente el
    /// juego de caracteres seguro como identificador en los 4 motores
    /// soportados (SQL Server, Postgres, MySQL, Mongo) sin depender
    /// únicamente del escape de identificadores que hace cada provisioner.
    /// No es la única defensa contra SQL injection — todas las consultas al
    /// catálogo usan parámetros tipados (<c>SqlParameter</c>) y los
    /// provisioners citan identificadores (<c>QuoteIdentifier</c>/backticks/
    /// comillas dobles según el motor) — pero validar el formato acá evita
    /// que un nombre "raro" (espacios, comillas, punto y coma, backticks)
    /// llegue siquiera a esa capa.
    /// </summary>
    [Required(ErrorMessage = "El nombre de la base de datos es obligatorio.")]
    [MaxLength(128)]
    [RegularExpression(@"^[a-zA-Z][a-zA-Z0-9_]{2,127}$",
        ErrorMessage = "El nombre solo puede contener letras, números y guion bajo, debe empezar con una letra y tener al menos 3 caracteres.")]
    public string DbName
    {
        get => _dbName;
        set => _dbName = InputNormalization.TrimOrEmpty(value);
    }

    /// <summary>
    /// Tope de conexiones simultáneas para el usuario/login de esta BD.
    /// Opcional — si se omite, se usa el default del motor
    /// (<c>Provisioning:{Engine}:MaxConcurrentConnections</c>). El backend
    /// SIEMPRE lo acota a <c>Provisioning:{Engine}:MaxConcurrentConnectionsCap</c>
    /// sin importar lo que pida el cliente, para que esto no deje de ser un
    /// control de abuso. Solo tiene efecto real en motores con soporte nativo
    /// (MySQL, Postgres); en SqlServer/Mongo se ignora (ver docs/bugs.md).
    /// </summary>
    [Range(1, 100)]
    public int? MaxConcurrentConnections { get; set; }
}

/// <summary>
/// Respuesta al crear una BD. Incluye el motor, los datos de conexión y las
/// credenciales generadas por el backend, que solo se muestran en este momento.
/// </summary>
public class CreateDatabaseResponse
{
    public int DatabaseId { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string DbName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int MaxStorageMB { get; set; }

    /// <summary>
    /// Tope de conexiones simultáneas efectivamente aplicado (ya acotado al
    /// cap del motor, puede diferir de lo pedido en el request). 0 en motores
    /// sin soporte nativo (SqlServer, Mongo) — ver docs/bugs.md ítem 12.
    /// </summary>
    public int MaxConcurrentConnections { get; set; }

    // A dónde conectarse.
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    // Credenciales (la contraseña en claro solo se entrega aquí, una vez).
    public string LoginName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>Representación de lectura de una BD del usuario autenticado.</summary>
public class DatabaseResponse
{
    public int DatabaseId { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string DbName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int MaxStorageMB { get; set; }
    public decimal CurrentSizeMB { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PausedAt { get; set; }
}
