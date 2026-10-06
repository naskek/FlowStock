using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class UpdateBootstrapServiceTests
{
    private const string SourceCommit = "1111111111111111111111111111111111111111";
    private const string TargetCommit = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task SourceRun_WithExistingActiveRuntime_UsesBootstrapPathInsteadOfInstalledIdentityCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-bootstrap-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new DesktopUpdatePaths(root, root);
            var state = new RuntimeStateManager(paths);
            var active = InstallActiveRuntime(paths, state);
            var runner = new BootstrapPathRunner();
            var service = new UpdateBootstrapService(paths, state, new GitRepositoryClient(runner), runner);

            var exception = await Assert.ThrowsAsync<BootstrapPathReachedException>(() =>
                service.PrepareAndLaunchAsync(
                    new Uri("https://flowstock.example/"),
                    Path.Combine(root, "repository"),
                    BuildIdentity.Create(active.ProductVersion, SourceCommit),
                    BuildIdentity.Create(active.ProductVersion, TargetCommit),
                    isSourceRun: true,
                    CancellationToken.None));

            Assert.Equal("source bootstrap reached", exception.Message);
            Assert.Equal(active, state.ReadActive()?.GetIdentity());
            Assert.True(File.Exists(paths.AppExecutable(active.SourceCommit)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstalledRun_WithMismatchedActiveRuntime_RemainsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-bootstrap-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new DesktopUpdatePaths(root, root);
            var state = new RuntimeStateManager(paths);
            var active = InstallActiveRuntime(paths, state);
            var runner = new BootstrapPathRunner();
            var service = new UpdateBootstrapService(paths, state, new GitRepositoryClient(runner), runner);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PrepareAndLaunchAsync(
                    new Uri("https://flowstock.example/"),
                    Path.Combine(root, "repository"),
                    BuildIdentity.Create(active.ProductVersion, SourceCommit),
                    BuildIdentity.Create(active.ProductVersion, TargetCommit),
                    isSourceRun: false,
                    CancellationToken.None));

            Assert.Equal("Запущенная assembly не совпадает с active runtime manifest.", exception.Message);
            Assert.Equal(0, runner.CallCount);
            Assert.Equal(active, state.ReadActive()?.GetIdentity());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static BuildIdentity InstallActiveRuntime(DesktopUpdatePaths paths, RuntimeStateManager state)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "FlowStock.App.exe");
        var identity = BuildIdentity.FromFile(source);
        Directory.CreateDirectory(paths.AppDirectory(identity.SourceCommit));
        File.Copy(source, paths.AppExecutable(identity.SourceCommit));
        state.WriteActive(identity);
        return identity;
    }

    private static void DeleteDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private sealed class BootstrapPathRunner : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new BootstrapPathReachedException();
        }
    }

    private sealed class BootstrapPathReachedException() : Exception("source bootstrap reached");
}
