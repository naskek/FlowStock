using System.Diagnostics;
using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class GitWorktreeIntegrationTests
{
    [Fact]
    public async Task DirtyStagedAndUntrackedCheckout_RemainsByteForByteUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flowstock-git-test-{Guid.NewGuid():N}");
        var worktree = Path.Combine(Path.GetTempPath(), $"flowstock-worktree-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.email", "flowstock-tests@example.invalid");
            RunGit(root, "config", "user.name", "FlowStock Tests");
            File.WriteAllBytes(Path.Combine(root, "tracked.bin"), [1, 2, 3, 4]);
            RunGit(root, "add", "tracked.bin");
            RunGit(root, "commit", "-m", "target");
            var target = RunGit(root, "rev-parse", "HEAD").Trim();

            File.WriteAllBytes(Path.Combine(root, "tracked.bin"), [9, 8, 7]);
            RunGit(root, "add", "tracked.bin");
            File.WriteAllBytes(Path.Combine(root, "tracked.bin"), [6, 5, 4, 3, 2, 1]);
            File.WriteAllBytes(Path.Combine(root, "untracked.bin"), [0, 255, 10, 13]);

            var beforeHead = RunGit(root, "rev-parse", "HEAD");
            var beforeBranch = RunGit(root, "symbolic-ref", "--short", "HEAD");
            var beforeStatus = RunGit(root, "status", "--porcelain=v1");
            var beforeIndex = RunGitBytes(root, "diff", "--cached", "--binary");
            var beforeWorking = File.ReadAllBytes(Path.Combine(root, "tracked.bin"));
            var beforeUntracked = File.ReadAllBytes(Path.Combine(root, "untracked.bin"));

            var client = new GitRepositoryClient(new ProcessRunner());
            await client.CreateDetachedWorktreeAsync(root, worktree, target, CancellationToken.None);
            Assert.Equal(target, RunGit(worktree, "rev-parse", "HEAD").Trim());
            await client.RemoveWorktreeAsync(root, worktree, CancellationToken.None);

            Assert.Equal(beforeHead, RunGit(root, "rev-parse", "HEAD"));
            Assert.Equal(beforeBranch, RunGit(root, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal(beforeStatus, RunGit(root, "status", "--porcelain=v1"));
            Assert.Equal(beforeIndex, RunGitBytes(root, "diff", "--cached", "--binary"));
            Assert.Equal(beforeWorking, File.ReadAllBytes(Path.Combine(root, "tracked.bin")));
            Assert.Equal(beforeUntracked, File.ReadAllBytes(Path.Combine(root, "untracked.bin")));
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                DeleteTestDirectory(worktree);
            }

            if (Directory.Exists(root))
            {
                DeleteTestDirectory(root);
            }
        }
    }

    private static void DeleteTestDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static string RunGit(string root, params string[] arguments) =>
        System.Text.Encoding.UTF8.GetString(RunGitBytes(root, arguments));

    private static byte[] RunGitBytes(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.ToArray();
    }
}
