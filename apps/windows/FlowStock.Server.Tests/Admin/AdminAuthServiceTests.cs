using FlowStock.App;

namespace FlowStock.Server.Tests.Admin;

public sealed class AdminAuthServiceTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _logPath;
    private readonly AdminAuthService _sut;

    public AdminAuthServiceTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"admin-auth-test-{Guid.NewGuid():N}.json");
        _logPath = Path.Combine(Path.GetTempPath(), $"admin-auth-test-{Guid.NewGuid():N}.log");
        _sut = new AdminAuthService(_tempPath, new FileLogger(_logPath));
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }

        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    [Fact]
    public void EnsureAdminPasswordExists_ReturnsFalse_WhenNoFile()
    {
        Assert.False(_sut.EnsureAdminPasswordExists());
    }

    [Fact]
    public void EnsureAdminPasswordExists_ReturnsTrue_AfterSetPassword()
    {
        _sut.SetPassword("correcthorse");

        Assert.True(_sut.EnsureAdminPasswordExists());
    }

    [Fact]
    public void VerifyPassword_ReturnsTrue_ForCorrectPassword()
    {
        _sut.SetPassword("correcthorse");

        Assert.True(_sut.VerifyPassword("correcthorse"));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalse_ForWrongPassword()
    {
        _sut.SetPassword("correcthorse");

        Assert.False(_sut.VerifyPassword("wrongpassword"));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalse_WhenNoFileExists()
    {
        Assert.False(_sut.VerifyPassword("anything"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetPassword_Throws_ForEmptyOrWhitespacePassword(string password)
    {
        Assert.Throws<ArgumentException>(() => _sut.SetPassword(password));
    }

    [Fact]
    public void SetPassword_OverwritesPrevious_AllowsNewPassword()
    {
        _sut.SetPassword("firstpassword");
        _sut.SetPassword("secondpassword");

        Assert.False(_sut.VerifyPassword("firstpassword"));
        Assert.True(_sut.VerifyPassword("secondpassword"));
    }
}
