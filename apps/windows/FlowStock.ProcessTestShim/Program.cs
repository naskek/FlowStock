using System.Diagnostics;
using System.Text.Json;

var outputPath = Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_OUTPUT");
var exitSignalPath = Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_EXIT_SIGNAL");
if (!string.IsNullOrWhiteSpace(outputPath))
{
    var invocation = new ProcessShimInvocation(Environment.CurrentDirectory, args, Environment.ProcessId, exitSignalPath);
    var temporaryOutputPath = $"{outputPath}.{Environment.ProcessId}.tmp";
    File.WriteAllText(temporaryOutputPath, JsonSerializer.Serialize(invocation));
    File.Move(temporaryOutputPath, outputPath, overwrite: true);
}

if (!string.IsNullOrWhiteSpace(exitSignalPath))
{
    var timeout = Stopwatch.StartNew();
    var ignoreExitSignal = Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_IGNORE_EXIT_SIGNAL") == "1";
    while ((ignoreExitSignal || !File.Exists(exitSignalPath)) && timeout.Elapsed < TimeSpan.FromSeconds(30))
    {
        Thread.Sleep(25);
    }
}

return int.TryParse(
    Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_EXIT_CODE"),
    out var exitCode)
    ? exitCode
    : 0;

internal sealed record ProcessShimInvocation(
    string WorkingDirectory,
    string[] Arguments,
    int ProcessId,
    string? ExitSignalPath);
