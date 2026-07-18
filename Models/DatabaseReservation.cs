namespace idempotencia.Models;

/// <summary>
/// Resultado (sin clave) del SP de control <c>sp_ReserveDatabase</c>: reserva un
/// registro en el catálogo (Status='Provisioning') tras validar cuota/límites y
/// devuelve los nombres generados que el provisioner usará para crear la BD.
/// </summary>
public class DatabaseReservation
{
    public int DatabaseId { get; set; }

    /// <summary>Nombre físico final de la BD (ya con prefijo por usuario).</summary>
    public string DbName { get; set; } = string.Empty;

    /// <summary>Login/usuario que el provisioner debe crear en el motor.</summary>
    public string LoginName { get; set; } = string.Empty;

    /// <summary>Cuota de almacenamiento a aplicar en la creación.</summary>
    public int MaxStorageMB { get; set; }
}
