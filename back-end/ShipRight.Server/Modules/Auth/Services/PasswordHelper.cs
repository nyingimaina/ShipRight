using System.Security.Cryptography;

namespace ShipRight.Modules.Auth.Services;

public interface IPasswordHelper
{
    string HashPassword(string password);
    bool VerifyPassword(string enteredPassword, string storedHash);
}

public class PasswordHelper : IPasswordHelper
{
    public string HashPassword(string password)
    {
        byte[] salt = GenerateSalt();
        byte[] hash = HashPasswordWithSalt(password, salt);

        byte[] hashBytes = new byte[salt.Length + hash.Length];
        Array.Copy(salt, 0, hashBytes, 0, salt.Length);
        Array.Copy(hash, 0, hashBytes, salt.Length, hash.Length);

        return Convert.ToBase64String(hashBytes);
    }

    public bool VerifyPassword(string enteredPassword, string storedHash)
    {
        byte[] hashBytes = Convert.FromBase64String(storedHash);

        byte[] salt = new byte[16];
        Array.Copy(hashBytes, 0, salt, 0, 16);

        byte[] enteredHash = HashPasswordWithSalt(enteredPassword, salt);

        byte[] storedHashBytes = new byte[20];
        Array.Copy(hashBytes, 16, storedHashBytes, 0, 20);

        return CryptographicOperations.FixedTimeEquals(storedHashBytes, enteredHash);
    }

    private static byte[] GenerateSalt(int size = 16)
    {
        byte[] salt = new byte[size];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    private static byte[] HashPasswordWithSalt(string password, byte[] salt, int iterations = 100_000)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(20);
    }
}
