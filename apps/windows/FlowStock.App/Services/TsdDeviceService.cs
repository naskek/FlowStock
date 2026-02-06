using System.Security.Cryptography;
using Npgsql;

namespace FlowStock.App;

public sealed class TsdDeviceService
{
    private const int DefaultIterations = 100_000;
    private readonly string _connectionString;
    private readonly FileLogger _logger;

    public TsdDeviceService(string connectionString, FileLogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public IReadOnlyList<TsdDeviceInfo> GetDevices()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, device_id, login, platform, is_active, created_at, last_seen
FROM tsd_devices
ORDER BY login;";
        using var reader = command.ExecuteReader();
        var list = new List<TsdDeviceInfo>();
        while (reader.Read())
        {
            var platform = reader.IsDBNull(3) ? "TSD" : reader.GetString(3);
            list.Add(new TsdDeviceInfo
            {
                Id = reader.GetInt64(0),
                DeviceId = reader.GetString(1),
                Login = reader.GetString(2),
                Platform = NormalizePlatform(platform),
                IsActive = reader.GetBoolean(4),
                CreatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                LastSeen = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return list;
    }

    public void AddDevice(string deviceId, string login, string password, bool isActive, string platform)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("ID устройства не задан.", nameof(deviceId));
        }
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new ArgumentException("Логин не задан.", nameof(login));
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Пароль не задан.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPassword(password, salt, DefaultIterations);
        var normalizedPlatform = NormalizePlatform(platform);

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash, password_iterations, platform, is_active, created_at)
VALUES(@device_id, @login, @salt, @hash, @iterations, @platform, @is_active, @created_at);";
        command.Parameters.AddWithValue("@device_id", deviceId.Trim());
        command.Parameters.AddWithValue("@login", login.Trim());
        command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        command.Parameters.AddWithValue("@iterations", DefaultIterations);
        command.Parameters.AddWithValue("@platform", normalizedPlatform);
        command.Parameters.AddWithValue("@is_active", isActive);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.ExecuteNonQuery();
        _logger.Info($"tsd_device_add device_id={deviceId} login={login} platform={normalizedPlatform}");
    }

    public void UpdateDevice(long id, string deviceId, string login, string? password, bool isActive, string platform)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Некорректный идентификатор устройства.", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("ID устройства не задан.", nameof(deviceId));
        }
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new ArgumentException("Логин не задан.", nameof(login));
        }

        var normalizedPlatform = NormalizePlatform(platform);

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        if (!string.IsNullOrWhiteSpace(password))
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(password, salt, DefaultIterations);
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE tsd_devices
SET device_id = @device_id,
    login = @login,
    platform = @platform,
    is_active = @is_active,
    password_salt = @salt,
    password_hash = @hash,
    password_iterations = @iterations
WHERE id = @id;";
            command.Parameters.AddWithValue("@device_id", deviceId.Trim());
            command.Parameters.AddWithValue("@login", login.Trim());
            command.Parameters.AddWithValue("@platform", normalizedPlatform);
            command.Parameters.AddWithValue("@is_active", isActive);
            command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
            command.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
            command.Parameters.AddWithValue("@iterations", DefaultIterations);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
        else
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE tsd_devices
SET device_id = @device_id,
    login = @login,
    platform = @platform,
    is_active = @is_active
WHERE id = @id;";
            command.Parameters.AddWithValue("@device_id", deviceId.Trim());
            command.Parameters.AddWithValue("@login", login.Trim());
            command.Parameters.AddWithValue("@platform", normalizedPlatform);
            command.Parameters.AddWithValue("@is_active", isActive);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        _logger.Info($"tsd_device_update id={id} device_id={deviceId} login={login} platform={normalizedPlatform} active={isActive}");
    }

    public void SetDeviceActive(long id, bool isActive)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tsd_devices SET is_active = @is_active WHERE id = @id;";
        command.Parameters.AddWithValue("@is_active", isActive);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        _logger.Info($"tsd_device_active id={id} active={isActive}");
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
    {
        using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
    }

    private static string NormalizePlatform(string? platform)
    {
        var normalized = string.IsNullOrWhiteSpace(platform) ? string.Empty : platform.Trim().ToUpperInvariant();
        return normalized == "PC" ? "PC" : "TSD";
    }
}

public sealed class TsdDeviceInfo
{
    public long Id { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string Platform { get; init; } = "TSD";
    public bool IsActive { get; init; }
    public string? CreatedAt { get; init; }
    public string? LastSeen { get; init; }

    public string PlatformDisplay => string.Equals(Platform, "PC", StringComparison.OrdinalIgnoreCase)
        ? "ПК"
        : "ТСД";
}
