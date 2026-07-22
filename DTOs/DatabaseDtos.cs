using System.ComponentModel.DataAnnotations;

namespace idempotencia.DTOs;

/// <summary>Datos de entrada para aprovisionar una nueva base de datos.</summary>
public class CreateDatabaseRequest
{
    /// <summary>
    /// Motor solicitado: "SqlServer", "Postgres", "MySql" o "Mongo"
    /// (ver <see cref="idempotencia.Models.DatabaseEngine"/>).
    /// </summary>
    [Required, MaxLength(20)]
    public string Engine { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string DbName { get; set; } = string.Empty;

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
