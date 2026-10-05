using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class ManagedGitRepositoryTests
{
    private const string InstalledCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string TargetCommit = "89abcdef0123456789abcdef0123456789abcdef";

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

    [Fact]
    public async Task ManagedRepository_AccessDeniedDuringPreparation_IsQuarantinedAndReclonedOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-managed-repo-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var pack = Path.Combine(repository, ".git", "objects", "pack", "pack-deadbeef.idx");
        var outside = Path.Combine(root, "outside-user-data.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        File.WriteAllText(pack, "old managed cache");
        File.WriteAllText(outside, "keep");
        var runner = new AccessDeniedThenHealthyRunner(repository);
        try
        {
            var client = new GitRepositoryClient(runner, repository);

            var diagnostics = await client.PrepareTargetAsync(
                repository,
                BuildIdentity.Create("1.0.0", InstalledCommit),
                BuildIdentity.Create("1.0.0", TargetCommit),
                CancellationToken.None);

            Assert.Equal(1, runner.AccessDeniedCount);
            Assert.Equal(1, runner.CloneCount);
            Assert.Equal(1, runner.FetchCount);
            Assert.Equal("## origin/main", diagnostics);
            Assert.True(Directory.Exists(repository));
            Assert.True(File.Exists(outside));
            Assert.Equal("keep", File.ReadAllText(outside));

            var quarantines = Directory.GetDirectories(root, "repository.quarantine-*");
            Assert.Single(quarantines);
            Assert.True(File.Exists(Path.Combine(quarantines[0], ".git", "objects", "pack", "pack-deadbeef.idx")));
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
    public async Task NonManagedRepository_AccessDenied_IsNotReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-managed-repo-{Guid.NewGuid():N}");
        var managedRepository = Path.Combine(root, "managed-repository");
        var sourceRepository = Path.Combine(root, "source-repository");
        Directory.CreateDirectory(Path.Combine(sourceRepository, ".git"));
        var sentinel = Path.Combine(sourceRepository, "do-not-delete.txt");
        File.WriteAllText(sentinel, "keep");
        var runner = new AccessDeniedThenHealthyRunner(sourceRepository);
        try
        {
            var client = new GitRepositoryClient(runner, managedRepository);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                client.PrepareTargetAsync(
                    sourceRepository,
                    BuildIdentity.Create("1.0.0", InstalledCommit),
                    BuildIdentity.Create("1.0.0", TargetCommit),
                    CancellationToken.None));

            Assert.Equal(1, runner.AccessDeniedCount);
            Assert.Equal(0, runner.CloneCount);
            Assert.True(File.Exists(sentinel));
            Assert.Empty(Directory.GetDirectories(root, "source-repository.quarantine-*"));
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

            if (args.Length >= 5 && args[0] == "-C" && args[2] == "remote" && args[3] == "get-url")
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

    private sealed class AccessDeniedThenHealthyRunner(string initiallyBrokenRepository) : IProcessRunner
    {
        private bool _accessDeniedThrown;

        public int AccessDeniedCount { get; private set; }
        public int CloneCount { get; private set; }
        public int FetchCount { get; private set; }

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
                Directory.CreateDirectory(Path.Combine(args[^1], ".git"));
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (args.Length >= 4 && args[0] == "-C" && args[2] == "rev-parse" && args[3] == "--show-toplevel")
            {
                if (!_accessDeniedThrown
                    && string.Equals(
                        Path.GetFullPath(args[1]),
                        Path.GetFullPath(initiallyBrokenRepository),
                        StringComparison.OrdinalIgnoreCase))
                {
                    _accessDeniedThrown = true;
                    AccessDeniedCount++;
                    throw new UnauthorizedAccessException("Access to the path 'pack-deadbeef.idx' is denied.");
                }

                return Task.FromResult(new ProcessResult(0, args[1] + Environment.NewLine, string.Empty));
            }

            if (args.Length >= 5 && args[0] == "-C" && args[2] == "remote" && args[3] == "get-url")
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    DesktopUpdateConstants.RepositoryUrl + Environment.NewLine,
                    string.Empty));
            }

            if (args.Length >= 3 && args[0] == "-C" && args[2] == "fetch")
            {
                FetchCount++;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (args.Length >= 5 && args[0] == "-C" && args[2] == "status")
            {
                return Task.FromResult(new ProcessResult(0, "## origin/main\n", string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
