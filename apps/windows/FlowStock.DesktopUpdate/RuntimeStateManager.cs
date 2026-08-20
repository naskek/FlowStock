using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FlowStock.DesktopUpdate;

public interface IRuntimeLauncher
{
    Process StartExecutable(string executable, IEnumerable<string> arguments, string workingDirectory);

    Process StartSourceRun(string repositoryRoot, IEnumerable<string> appArguments);
}

public sealed class RuntimeLauncher : IRuntimeLauncher
{
    public Process StartExecutable(string executable, IEnumerable<string> arguments, string workingDirectory) =>
        RuntimeStateManager.Start(executable, arguments, workingDirectory);

    public Process StartSourceRun(string repositoryRoot, IEnumerable<string> appArguments)
    {
        var arguments = new List<string>
        {
            "run",
            "--project",
            Path.Combine(repositoryRoot, "apps", "windows", "FlowStock.App", "FlowStock.App.csproj"),
            "--"
        };
        arguments.AddRange(appArguments);
        return RuntimeStateManager.Start("dotnet", arguments, repositoryRoot);
    }
}

public sealed class RuntimeStateManager
{
    private readonly DesktopUpdatePaths _paths;

    public RuntimeStateManager(DesktopUpdatePaths paths)
    {
        _paths = paths;
    }

    public RuntimeManifest? ReadActive() => ReadValidated(_paths.ActiveManifest);
    public RuntimeManifest? ReadLastKnownGood() => ReadValidated(_paths.LastKnownGoodManifest);

    public RuntimeManifest? ReadValidated(string path)
    {
        var manifest = JsonStateStore.Read<RuntimeManifest>(path);
        if (manifest is null)
        {
            return null;
        }

        if (manifest.SchemaVersion != DesktopUpdateConstants.ProtocolVersion)
        {
            throw new InvalidOperationException("Версия runtime manifest не поддерживается.");
        }

        var identity = manifest.GetIdentity();
        var executable = _paths.AppExecutable(identity.SourceCommit);
        if (!File.Exists(executable) || BuildIdentity.FromFile(executable) != identity)
        {
            throw new InvalidOperationException("Runtime manifest не соответствует установленной App assembly.");
        }

        return manifest;
    }

    public void WriteActive(BuildIdentity identity) =>
        JsonStateStore.WriteAtomic(_paths.ActiveManifest, ToManifest(identity));

    public void WriteLastKnownGood(BuildIdentity identity) =>
        JsonStateStore.WriteAtomic(_paths.LastKnownGoodManifest, ToManifest(identity));

    public void ClearActive()
    {
        if (File.Exists(_paths.ActiveManifest))
        {
            File.Delete(_paths.ActiveManifest);
        }
    }

    public static RuntimeManifest ToManifest(BuildIdentity identity) =>
        new(DesktopUpdateConstants.ProtocolVersion, identity.ProductVersion, identity.SourceCommit);

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bundle(string directory)
    {
        var lines = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(directory, path).Replace('\\', '/').ToLowerInvariant()}:{Sha256File(path)}\n")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines))))
            .ToLowerInvariant();
    }

    public static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {executable}.");
    }
}
