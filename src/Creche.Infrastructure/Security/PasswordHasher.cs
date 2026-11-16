using System.Security.Cryptography;

namespace Creche.Infrastructure.Security;

/// <summary>PBKDF2-HMAC-SHA256, 100,000 iterations, 16-byte random salt, 32-byte key, constant-time comparison.</summary>
public static class PasswordHasher
{
    public const int Iterations = 100_000;
    public const int SaltSize = 16;
    public const int KeySize = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), Iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        try
        {
            byte[] expected = Convert.FromBase64String(hash);
            byte[] saltBytes = Convert.FromBase64String(salt);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations <= 0 ? Iterations : iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
