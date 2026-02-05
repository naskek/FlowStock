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
SELECT id, device_id, login, is_active, created_at, last_seen
FROM tsd_devices
ORDER BY login;";
        using var reader = command.ExecuteReader();
        var list = new List<TsdDeviceInfo>();
        while (reader.Read())
        {
            list.Add(new TsdDeviceInfo
            {
                Id = reader.GetInt64(0),
                DeviceId = reader.GetString(1),
                Login = reader.GetString(2),
                IsActive = reader.GetBoolean(3),
                CreatedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
                LastSeen = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return list;
    }

    public void AddDevice(string deviceId, string login, string password, bool isActive)
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

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO tsd_devices(device_id, login, password_salt, password_hash, password_iterations, is_active, created_at)
VALUES(@device_id, @login, @salt, @hash, @iterations, @is_active, @created_at);";
        command.Parameters.AddWithValue("@device_id", deviceId.Trim());
        command.Parameters.AddWithValue("@login", login.Trim());
        command.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        command.Parameters.AddWithValue("@iterations", DefaultIterations);
        command.Parameters.AddWithValue("@is_active", isActive);
        command.Parameters.AddWithValue("@created_at", DateTime.Now.ToString("s"));
        command.ExecuteNonQuery();
        _logger.Info($"tsd_device_add device_id={deviceId} login={login}");
    }

    public void UpdateDevice(long id, string deviceId, string login, string? password, bool isActive)
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
    is_active = @is_active,
    password_salt = @salt,
    password_hash = @hash,
    password_iterations = @iterations
WHERE id = @id;";
            command.Parameters.AddWithValue("@device_id", deviceId.Trim());
            command.Parameters.AddWithValue("@login", login.Trim());
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
    is_active = @is_active
WHERE id = @id;";
            command.Parameters.AddWithValue("@device_id", deviceId.Trim());
            command.Parameters.AddWithValue("@login", login.Trim());
            command.Parameters.AddWithValue("@is_active", isActive);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        _logger.Info($"tsd_device_update id={id} device_id={deviceId} login={login} active={isActive}");
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
}

public sealed class TsdDeviceInfo
{
    public long Id { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string? CreatedAt { get; init; }
    public string? LastSeen { get; init; }
}
