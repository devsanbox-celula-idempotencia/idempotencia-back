namespace idempotencia.Models;

/// <summary>
/// Resultado de una rotación de credenciales hecha por un provisioner que NO
/// acepta una contraseña impuesta desde afuera.
///
/// Existe por la API externa de MongoDB: su endpoint de rotación
/// (<c>POST /databases/{id}/credentials/reset</c>) genera la contraseña él
/// mismo y la devuelve; no hay forma de decirle "usa esta". Los provisioners
/// locales sí aplican la contraseña que reciben, y por eso devuelven
/// <c>null</c> en vez de una instancia de este tipo: <c>null</c> significa
/// literalmente "se aplicó la que me pasaste, no hay nada nuevo que contarte".
///
/// La distinción importa porque el orquestador guarda el HASH de la contraseña
/// y se la envía al usuario por correo: si guardara la que generó el backend
/// cuando la que quedó vigente es otra, el usuario recibiría una credencial que
/// no funciona y el catálogo tendría un hash que nunca podría validar.
/// </summary>
/// <param name="Password">Contraseña que quedó realmente vigente en el motor.</param>
/// <param name="ConnectionUri">
/// Cadena de conexión ya armada por el servicio externo con esa contraseña, si
/// la devolvió. <c>null</c> si hay que construirla con
/// <c>IDatabaseProvisioner.BuildClientConnection</c>.
/// </param>
public record CredentialRotationResult(string Password, string? ConnectionUri);
