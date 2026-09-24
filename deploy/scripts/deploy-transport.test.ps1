Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'deploy-transport.ps1')

$pwsh = (Get-Process -Id $PID).Path
$payload = [byte[]]@(0, 10, 13, 65, 66, 67, 127, 128, 255)
$expected = [Convert]::ToBase64String($payload)

$childCommand = @'
$inputStream = [Console]::OpenStandardInput()
$buffer = [System.IO.MemoryStream]::new()
try {
    $inputStream.CopyTo($buffer)
    [Console]::Out.Write([Convert]::ToBase64String($buffer.ToArray()))
}
finally {
    $buffer.Dispose()
    $inputStream.Dispose()
}
'@

$result = Invoke-ProcessWithRawStdin -FilePath $pwsh -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $childCommand) -StdinBytes $payload

if ($result.ExitCode -ne 0) {
    throw "Raw stdin child exited with code $($result.ExitCode): $($result.StdErr)"
}
if ($result.StdOut -ne $expected) {
    throw "Raw stdin bytes changed in transit. Expected=$expected Actual=$($result.StdOut)"
}
if ($result.StdErr) {
    throw "Raw stdin child produced unexpected stderr: $($result.StdErr)"
}

Write-Host 'deploy transport runtime test passed'
