namespace idempotencia.Models;

/// <summary>
/// Tipos de registro DNS que la plataforma sabe crear. Es el equivalente de
/// <see cref="DatabaseEngine"/> para el servicio de DNS.
///
/// El autoservicio de subdominios solo crea <see cref="A"/>: el usuario aporta
/// la IPv4 pública donde corre su servicio. Los otros están declarados porque la
/// tabla del catálogo ya los admite, pero habilitarlos no es solo relajar una
/// validación — cada uno necesita su propia forma de validar el destino (un
/// CNAME hacia infraestructura ajena, por ejemplo, no se comprueba igual que
/// una IP).
/// </summary>
public static class DnsRecordType
{
    public const string A = "A";
    public const string Aaaa = "AAAA";
    public const string Cname = "CNAME";
    public const string Txt = "TXT";
}
