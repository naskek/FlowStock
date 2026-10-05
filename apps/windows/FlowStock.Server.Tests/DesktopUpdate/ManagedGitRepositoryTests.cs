using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class ManagedGitRepositoryTests
{
    [Fact]
    public async Task MissingManagedRepository_IsClonedFromCanonicalRemote()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-managed-repo-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var runner = new BootstrapRunner();
        try
        {
            var client = new GitRepositoryClient(runner);

            await client.EnsureManagedRepositoryAsync(repository, CancellationToken.None);

            Assert.True(Directory.Exists(repository));
            Assert.Equal(1, runner.CloneCount);
            Assert.Equal(DesktopUpdateConstants.RepositoryUrl, runner.CloneRepositoryUrl);
            Assert.Equal(root, runner.CloneWorkingDirectory);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExistingInvalidManagedRepository_IsNotReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-managed-repo-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        var sentinel = Path.Combine(repository, "do-not-delete.txt");
        File.WriteAllText(sentinel, "keep");
        var runner = new InvalidRepositoryRunner();
        try
        {
            var client = new GitRepositoryClient(runner);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.EnsureManagedRepositoryAsync(repository, CancellationToken.None));

            Assert.True(File.Exists(sentinel));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal(0, runner.CloneCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class BootstrapRunner : IProcessRunner
    {
        public int CloneCount { get; private set; }
        public string? CloneRepositoryUrl { get; private set; }
        public string? CloneWorkingDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Assert.Equal("git", fileName);
            if (args.Length > 0 && args[0] == "clone")
            {
                CloneCount++;
                CloneRepositoryUrl = args[^2];
                CloneWorkingDirectory = workingDirectory;
                var destination = args[^1];
                Directory.CreateDirectory(Path.Combine(destination, ".git"));
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (args.Length >= 4 && args[0] == "-C" && args[2] == "rev-parse" && args[3] == "--show-toplevel")
            {
                return Task.FromResult(new ProcessResult(0, args[1] + Environment.NewLine, string.Empty));
            }

            if (args.Length >= 6 && args[0] == "-C" && args[2] == "remote" && args[3] == "get-url")
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    DesktopUpdateConstants.RepositoryUrl + Environment.NewLine,
                    string.Empty));
            }

            return Task.FromResult(new ProcessResult(1, string.Empty, "unexpected git invocation"));
        }
    }

    private sealed class InvalidRepositoryRunner : IProcessRunner
    {
        public int CloneCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (args.Length > 0 && args[0] == "clone")
            {
                CloneCount++;
            }

            return Task.FromResult(new ProcessResult(1, string.Empty, "not a FlowStock repository"));
        }
    }
}
