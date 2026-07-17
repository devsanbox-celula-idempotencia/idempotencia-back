namespace idempotencia.Models;

/// <summary>
/// Tipo de resultado (sin clave) devuelto por <c>sp_CreateDatabase</c>.
/// El SP aprovisiona la BD (valida cuota y límite de BDs por usuario, crea el
/// login) y devuelve el identificador junto con las credenciales de acceso.
/// La contraseña en claro (<see cref="Password"/>) solo se entrega UNA vez al
/// crear la BD; el backend nunca la persiste.
/// </summary>
public class NewDatabaseResult
{
    public int DatabaseId { get; set; }
    public string DbName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public int MaxStorageMB { get; set; }

    // Credenciales devueltas por el SP para conectarse a la BD recién creada.
    public string LoginName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
