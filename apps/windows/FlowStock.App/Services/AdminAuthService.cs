using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowStock.App;

public sealed class AdminAuthService
{
    private const int DefaultIterations = 100_000;
    private readonly string _adminPath;
    private readonly FileLogger _logger;

    public AdminAuthService(string adminPath, FileLogger logger)
    {
        _adminPath = adminPath;
        _logger = logger;
    }

    public bool EnsureAdminPasswordExists()
    {
        return File.Exists(_adminPath);
    }

    public void SetPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPassword(password, salt, DefaultIterations);
        var record = new AdminAuthRecord
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = DefaultIterations
        };

        var dir = Path.GetDirectoryName(_adminPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_adminPath, json);
        _logger.Info("Admin password set");
    }

    public bool VerifyPassword(string password)
    {
        if (!File.Exists(_adminPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_adminPath);
            var record = JsonSerializer.Deserialize<AdminAuthRecord>(json);
            if (record == null || string.IsNullOrWhiteSpace(record.Salt) || string.IsNullOrWhiteSpace(record.Hash))
            {
                return false;
            }

            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            var iterations = record.Iterations > 0 ? record.Iterations : DefaultIterations;
            var actual = HashPassword(password, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Admin password verify failed: {ex.Message}");
            return false;
        }
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
    {
        using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
    }

    private sealed class AdminAuthRecord
    {
        public string Salt { get; init; } = string.Empty;
        public string Hash { get; init; } = string.Empty;
        public int Iterations { get; init; }
    }
}

