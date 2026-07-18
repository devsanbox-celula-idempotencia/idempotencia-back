using System.Security.Cryptography;

namespace idempotencia.Services;

/// <summary>
/// Genera contraseñas aleatorias fuertes para las credenciales de BD que se
/// crean por motor. Usa un RNG criptográfico y garantiza variedad de caracteres.
/// </summary>
public static class PasswordGenerator
{
    // Se excluyen caracteres ambiguos (0/O, 1/l/I) para evitar confusiones.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Special = "#$%&*+-";
    private const string All = Upper + Lower + Digits + Special;

    public static string Generate(int length = 24)
    {
        if (length < 8) length = 8;

        var chars = new char[length];

        // Garantiza al menos uno de cada categoría (complejidad).
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Special);
        for (var i = 4; i < length; i++)
            chars[i] = Pick(All);

        // Mezcla (Fisher-Yates) para no dejar las categorías fijas al inicio.
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
