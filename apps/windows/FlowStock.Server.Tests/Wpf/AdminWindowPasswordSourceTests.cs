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
    public void ClearOperations_RequiresAdminPasswordBeforeResetMovements()
    {
        var handler = ExtractMethodBody(
            ReadAdminWindowCode(),
            "private void ClearOperations_Click");

        Assert.Contains("_services.AdminAuth.EnsureAdminPasswordExists()", handler, StringComparison.Ordinal);
        Assert.Contains("SetAdminPasswordWindow", handler, StringComparison.Ordinal);

        var promptIndex = handler.IndexOf("PasswordPromptWindow", StringComparison.Ordinal);
        var resetIndex = handler.IndexOf("_services.Admin.ResetMovements()", StringComparison.Ordinal);

        Assert.True(promptIndex >= 0, "ClearOperations_Click must show PasswordPromptWindow.");
        Assert.True(resetIndex >= 0, "ClearOperations_Click must call ResetMovements.");
        Assert.True(
            promptIndex < resetIndex,
            "Password prompt must be invoked before _services.Admin.ResetMovements().");
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
