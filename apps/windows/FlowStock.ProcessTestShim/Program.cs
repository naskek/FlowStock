using System.Text.Json;

var outputPath = Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_OUTPUT");
if (!string.IsNullOrWhiteSpace(outputPath))
{
    var invocation = new ProcessShimInvocation(Environment.CurrentDirectory, args);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(invocation));
}

return int.TryParse(
    Environment.GetEnvironmentVariable("FLOWSTOCK_PROCESS_SHIM_EXIT_CODE"),
    out var exitCode)
    ? exitCode
    : 0;

internal sealed record ProcessShimInvocation(string WorkingDirectory, string[] Arguments);
