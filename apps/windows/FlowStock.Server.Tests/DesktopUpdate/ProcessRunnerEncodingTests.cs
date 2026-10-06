using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class ProcessRunnerEncodingTests
{
    [Fact]
    public async Task GitRepositoryRoot_WithUnicodeWindowsPath_IsDecodedAsUtf8()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"flowstock-ЧестныйЗнак-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);

        var runner = new ProcessRunner();
        try
        {
            var init = await runner.RunAsync(
                "git",
                ["init", "--initial-branch=main", repository],
                root,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            Assert.True(init.Success, init.StandardError);

            var remote = await runner.RunAsync(
                "git",
                ["-C", repository, "remote", "add", DesktopUpdateConstants.RemoteName, DesktopUpdateConstants.RepositoryUrl],
                root,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            Assert.True(remote.Success, remote.StandardError);

            var gitRoot = await runner.RunAsync(
                "git",
                ["-C", repository, "rev-parse", "--show-toplevel"],
                root,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            Assert.True(gitRoot.Success, gitRoot.StandardError);

            Assert.True(
                string.Equals(
                    NormalizePath(repository),
                    NormalizePath(gitRoot.StandardOutput.Trim()),
                    StringComparison.OrdinalIgnoreCase),
                $"Expected Git root '{repository}', got '{gitRoot.StandardOutput.Trim()}'.");

            var client = new GitRepositoryClient(runner, repository);
            await client.ValidateRepositoryAndRemoteAsync(repository, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
