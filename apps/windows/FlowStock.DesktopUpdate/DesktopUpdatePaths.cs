namespace FlowStock.DesktopUpdate;

public sealed class DesktopUpdatePaths
{
    public DesktopUpdatePaths(string? localAppData = null, string? roamingAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roamingAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = Path.Combine(localAppData, "FlowStock", "Desktop");
        Versions = Path.Combine(Root, "versions");
        Bootstrap = Path.Combine(Root, "bootstrap");
        Transactions = Path.Combine(Root, "transactions");
        State = Path.Combine(Root, "state");
        Results = Path.Combine(State, "results");
        ActiveManifest = Path.Combine(State, "active-runtime.json");
        LastKnownGoodManifest = Path.Combine(State, "last-known-good.json");
        PendingTransaction = Path.Combine(State, "pending-transaction.json");
        Logs = Path.Combine(roamingAppData, "FlowStock", "Logs", "Updates");
    }

    public string Root { get; }
    public string Versions { get; }
    public string Bootstrap { get; }
    public string Transactions { get; }
    public string State { get; }
    public string Results { get; }
    public string ActiveManifest { get; }
    public string LastKnownGoodManifest { get; }
    public string PendingTransaction { get; }
    public string Logs { get; }

    public string VersionRoot(string commit) => Under(Root, Path.Combine("versions", ValidateCommit(commit)));
    public string AppDirectory(string commit) => Path.Combine(VersionRoot(commit), "app");
    public string AppExecutable(string commit) => Path.Combine(AppDirectory(commit), "FlowStock.App.exe");
    public string UpdaterDirectory(string commit) => Path.Combine(VersionRoot(commit), "updater");
    public string UpdaterExecutable(string commit) => Path.Combine(UpdaterDirectory(commit), "FlowStock.Updater.exe");
    public string InstallManifest(string commit) => Path.Combine(VersionRoot(commit), "install-manifest.json");
    public string TransactionRoot(string sessionId) => Under(Root, Path.Combine("transactions", ValidateSession(sessionId)));
    public string RecoveryDirectory(string sessionId) => Path.Combine(TransactionRoot(sessionId), "recovery");
    public string RecoveryExecutable(string sessionId) => Path.Combine(RecoveryDirectory(sessionId), "FlowStock.Updater.exe");
    public string RequestFile(string sessionId) => Path.Combine(TransactionRoot(sessionId), "request.json");
    public string ReadyFile(string sessionId) => Path.Combine(TransactionRoot(sessionId), "updater-ready");
    public string StartupAckFile(string sessionId) => Path.Combine(TransactionRoot(sessionId), "startup-ack.json");
    public string ResultFile(string sessionId) => Path.Combine(Results, $"{ValidateSession(sessionId)}.json");
    public string LogFile(string sessionId) => Path.Combine(Logs, $"{ValidateSession(sessionId)}.log");

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(Versions);
        Directory.CreateDirectory(Bootstrap);
        Directory.CreateDirectory(Transactions);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Results);
        Directory.CreateDirectory(Logs);
    }

    private string Under(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update path выходит за пределы runtime root.");
        }

        return candidate;
    }

    private static string ValidateCommit(string commit) =>
        BuildIdentity.IsFullCommit(commit)
            ? commit
            : throw new InvalidOperationException("Некорректный commit в runtime manifest.");

    private static string ValidateSession(string sessionId) =>
        Guid.TryParseExact(sessionId, "N", out _)
            ? sessionId
            : throw new InvalidOperationException("Некорректный update session id.");
}
