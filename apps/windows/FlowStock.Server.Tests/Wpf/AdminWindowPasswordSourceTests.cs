namespace FlowStock.Server.Tests.Wpf;

public sealed class AdminWindowPasswordSourceTests
{
    [Fact]
    public void AdminWindow_ExposesChangeAdminPasswordButton()
    {
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "AdminWindow.xaml"));

        Assert.Contains("Сменить пароль администратора...", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ChangeAdminPassword_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminWindow_DoesNotExposeDestructiveDatabaseReset()
    {
        var xaml = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "AdminWindow.xaml"));
        var code = ReadAdminWindowCode();
        var mainWindowCode = File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "MainWindow.xaml.cs"));

        Assert.DoesNotContain("Очистка перед стартом", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Очистить операции", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearOperations_Click", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetMovements", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FullReset", code, StringComparison.Ordinal);
        Assert.DoesNotContain("onOperationsCleared", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetMovements", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FullReset", mainWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeAdminPassword_VerifiesCurrentPasswordWhenAlreadySet()
    {
        var handler = ExtractMethodBody(
            ReadAdminWindowCode(),
            "private void ChangeAdminPassword_Click");

        var ensureIndex = handler.IndexOf("EnsureAdminPasswordExists()", StringComparison.Ordinal);
        var promptIndex = handler.IndexOf("PasswordPromptWindow", StringComparison.Ordinal);

        Assert.True(ensureIndex >= 0, "ChangeAdminPassword_Click must check whether a password already exists.");
        Assert.True(promptIndex >= 0, "ChangeAdminPassword_Click must verify the current password via PasswordPromptWindow.");
        Assert.True(
            ensureIndex < promptIndex,
            "Current-password verification must be gated by the existing-password check.");
        Assert.Contains("SetAdminPasswordWindow", handler, StringComparison.Ordinal);
    }

    private static string ReadAdminWindowCode()
    {
        return File.ReadAllText(GetRepoFile("apps", "windows", "FlowStock.App", "AdminWindow.xaml.cs"));
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");

        var nextMethod = source.IndexOf("    private ", start + signature.Length, StringComparison.Ordinal);
        var end = nextMethod >= 0 ? nextMethod : source.Length;
        return source.Substring(start, end - start);
    }

    private static string GetRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"File not found: {string.Join(Path.DirectorySeparatorChar, parts)}");
    }
}
