using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class RuntimeStateTests
{
    [Fact]
    public void Paths_UseManagedRepositoryUnderDesktopRoot()
    {
        var local = Path.Combine(Path.GetTempPath(), $"flowstock-path-test-{Guid.NewGuid():N}");
        var paths = new DesktopUpdatePaths(local, local);

        Assert.Equal(Path.Combine(local, "FlowStock", "Desktop", "repository"), paths.Repository);
        Assert.Equal(paths.Repository, DesktopUpdateConstants.DefaultRepositoryRoot.Replace(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            local,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Paths_RejectCommitAndSessionTraversal()
    {
        var paths = new DesktopUpdatePaths(Path.GetTempPath(), Path.GetTempPath());

        Assert.Throws<InvalidOperationException>(() => paths.VersionRoot("..\\escape"));
        Assert.Throws<InvalidOperationException>(() => paths.TransactionRoot("..\\escape"));
    }

    [Fact]
    public void AtomicJsonState_RoundTripsManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-update-test-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "state.json");
        try
        {
            var manifest = new RuntimeManifest(
                1,
                "1.0.0",
                "0123456789abcdef0123456789abcdef01234567");
            JsonStateStore.WriteAtomic(path, manifest);

            Assert.Equal(manifest, JsonStateStore.Read<RuntimeManifest>(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
