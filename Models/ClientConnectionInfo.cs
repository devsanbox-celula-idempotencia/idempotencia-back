namespace idempotencia.Models;

/// <summary>
/// Cadenas de conexión ya armadas para que el usuario final las pegue en su
/// cliente, con el parámetro de TLS del motor ya incluido. Existe para que el
/// usuario no tenga que deducir la sintaxis correcta de cada driver: el caso
/// que la motivó es MySQL, donde <c>caching_sha2_password</c> (el plugin de
/// autenticación por defecto desde MySQL 8) exige un intercambio de clave RSA
/// si la conexión NO está cifrada, y los clientes lo resuelven pidiéndole al
/// usuario activar <c>allowPublicKeyRetrieval</c> a mano — un paso extra, y
/// además peor para la seguridad (habilita un MITM que capture la contraseña en
/// claro). Con la conexión cifrada, ese intercambio ocurre dentro del canal TLS
/// y el parámetro deja de hacer falta.
/// </summary>
/// <param name="Uri">
/// URI en el formato nativo del motor, con credenciales incluidas — sirve para
/// clientes de consola (<c>mysql</c>, <c>psql</c>, <c>mongosh</c>) y para
/// pegarla en herramientas que aceptan una URI completa. Usuario y contraseña
/// van percent-encoded: las contraseñas generadas incluyen <c>#$%&amp;*+-</c>, que
/// romperían la URI si se interpolaran en crudo.
/// </param>
/// <param name="JdbcUrl">
/// URL JDBC equivalente, para clientes de escritorio basados en Java
/// (DBeaver, MySQL Workbench, DataGrip) que ofrecen "conectar por URL". Va
/// SIN credenciales, porque esos clientes las piden en campos aparte. Es
/// <c>null</c> en MongoDB, que no tiene un driver JDBC estándar.
/// </param>
public record ClientConnectionInfo(string Uri, string? JdbcUrl);
