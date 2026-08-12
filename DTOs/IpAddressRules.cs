using System.Net;
using System.Net.Sockets;

namespace idempotencia.DTOs;

/// <summary>
/// Reglas de validación para la IPv4 de destino de un subdominio.
///
/// No alcanza con "que parsee": el registro se crea <b>proxeado</b> a través de
/// Cloudflare, y eso significa que quien se conecta al origen es el borde de
/// Cloudflare desde internet, no el navegador del usuario. Una IP privada,
/// de loopback o de CGNAT es perfectamente válida como IPv4 y perfectamente
/// inalcanzable desde ahí: el subdominio se crearía sin error y fallaría
/// después, en el navegador de quien lo visite, con un error 522 del borde que
/// no le dice a nadie cuál fue la causa real.
///
/// Rechazarlas en el momento de crear el registro convierte ese fallo diferido y
/// opaco en un 400 inmediato y explicable.
///
/// Nota para este despliegue: <c>Provisioning:IpVps</c> es hoy una dirección
/// <c>100.64.0.0/10</c> (rango CGNAT, típico de Tailscale). Esa IP NO pasa esta
/// validación, y con razón — Cloudflare no puede alcanzarla. Si en algún momento
/// se quiere que los usuarios apunten a la propia plataforma, hay que darles una
/// IP pública de verdad, no la de la red privada.
/// </summary>
internal static class IpAddressRules
{
    /// <summary>
    /// <c>true</c> si el texto es una IPv4 en notación decimal punteada y
    /// pertenece al espacio de direcciones enrutable en internet público.
    /// </summary>
    public static bool IsPublicIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // IPAddress.TryParse acepta formas que nadie quiere acá (notación
        // hexadecimal, octal, enteros de 32 bits sin puntos: "0x7f.1", "2130706433").
        // Todas resuelven a una IP real, así que sin esta comprobación previa un
        // usuario podría escribir "2130706433" y crear un registro a 127.0.0.1
        // esquivando el filtro de rangos de abajo. Se exige la forma canónica de
        // cuatro octetos decimales.
        var parts = value.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3)
                return false;

            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                    return false;
            }

            if (!byte.TryParse(part, out _))
                return false;
        }

        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = ip.GetAddressBytes();
        return !IsReservedRange(b);
    }

    /// <summary>
    /// Rangos que no son enrutables en internet público, según IANA. La lista es
    /// explícita y comentada en vez de un par de condiciones "aproximadas"
    /// porque cada omisión acá es un subdominio que se crea bien y no funciona.
    /// </summary>
    private static bool IsReservedRange(byte[] b) => b[0] switch
    {
        0 => true,                                   // 0.0.0.0/8      "esta red"
        10 => true,                                  // 10.0.0.0/8     privada (RFC 1918)
        127 => true,                                 // 127.0.0.0/8    loopback
        100 when b[1] >= 64 && b[1] <= 127 => true,  // 100.64.0.0/10  CGNAT (RFC 6598)
        169 when b[1] == 254 => true,                // 169.254.0.0/16 link-local
        172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12  privada (RFC 1918)
        192 when b[1] == 168 => true,                // 192.168.0.0/16 privada (RFC 1918)
        192 when b[1] == 0 && b[2] == 0 => true,     // 192.0.0.0/24   asignaciones IETF
        192 when b[1] == 0 && b[2] == 2 => true,     // 192.0.2.0/24   documentación
        198 when b[1] is 18 or 19 => true,           // 198.18.0.0/15  pruebas de rendimiento
        198 when b[1] == 51 && b[2] == 100 => true,  // 198.51.100.0/24 documentación
        203 when b[1] == 0 && b[2] == 113 => true,   // 203.0.113.0/24 documentación
        >= 224 => true,                              // 224.0.0.0/4 multicast y 240.0.0.0/4 reservado
        _ => false
    };
}
