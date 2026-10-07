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

$stdoutGate = Join-Path $env:TEMP "flowstock-stream-stdout-$([Guid]::NewGuid().ToString('N')).gate"
$stderrGate = Join-Path $env:TEMP "flowstock-stream-stderr-$([Guid]::NewGuid().ToString('N')).gate"
$streamedStdOut = [System.Text.StringBuilder]::new()
$streamedStdErr = [System.Text.StringBuilder]::new()

$streamingChildCommand = @'
[Console]::Out.WriteLine('stdout-before-exit')
[Console]::Out.Flush()
[Console]::Error.WriteLine('stderr-before-exit')
[Console]::Error.Flush()

$deadline = [DateTime]::UtcNow.AddSeconds(10)
while (-not (
    (Test-Path -LiteralPath $env:FLOWSTOCK_TEST_STDOUT_GATE) -and
    (Test-Path -LiteralPath $env:FLOWSTOCK_TEST_STDERR_GATE)
)) {
    if ([DateTime]::UtcNow -ge $deadline) {
        exit 23
    }
    Start-Sleep -Milliseconds 25
}

[Console]::Out.Write('stdout-after-gates')
[Console]::Out.Flush()
[Console]::Error.Write('stderr-after-gates')
[Console]::Error.Flush()
'@

$stdoutHandler = {
    param([string]$Chunk)

    [void]$streamedStdOut.Append($Chunk)
    if ($streamedStdOut.ToString().Contains('stdout-before-exit') -and
        -not (Test-Path -LiteralPath $stdoutGate)) {
        Set-Content -LiteralPath $stdoutGate -Value 'seen' -Encoding ascii -NoNewline
    }
}.GetNewClosure()

$stderrHandler = {
    param([string]$Chunk)

    [void]$streamedStdErr.Append($Chunk)
    if ($streamedStdErr.ToString().Contains('stderr-before-exit') -and
        -not (Test-Path -LiteralPath $stderrGate)) {
        Set-Content -LiteralPath $stderrGate -Value 'seen' -Encoding ascii -NoNewline
    }
}.GetNewClosure()

try {
    $env:FLOWSTOCK_TEST_STDOUT_GATE = $stdoutGate
    $env:FLOWSTOCK_TEST_STDERR_GATE = $stderrGate

    $streamResult = Invoke-ProcessWithRawStdin `
        -FilePath $pwsh `
        -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $streamingChildCommand) `
        -StdinBytes ([byte[]]@(0)) `
        -StdOutHandler $stdoutHandler `
        -StdErrHandler $stderrHandler

    if ($streamResult.ExitCode -ne 0) {
        throw "Streaming child exited with code $($streamResult.ExitCode): $($streamResult.StdErr)"
    }
    if (-not (Test-Path -LiteralPath $stdoutGate) -or -not (Test-Path -LiteralPath $stderrGate)) {
        throw 'stdout/stderr handlers did not observe output before child exit'
    }
    if ($streamResult.StdOut -notmatch '^stdout-before-exit\r?\nstdout-after-gates$') {
        throw "Captured stdout changed during streaming: $($streamResult.StdOut)"
    }
    if ($streamResult.StdErr -notmatch '^stderr-before-exit\r?\nstderr-after-gates$') {
        throw "Captured stderr changed during streaming: $($streamResult.StdErr)"
    }
    if ($streamedStdOut.ToString() -ne $streamResult.StdOut) {
        throw 'Streamed stdout differs from captured stdout'
    }
    if ($streamedStdErr.ToString() -ne $streamResult.StdErr) {
        throw 'Streamed stderr differs from captured stderr'
    }
}
finally {
    Remove-Item Env:FLOWSTOCK_TEST_STDOUT_GATE -ErrorAction SilentlyContinue
    Remove-Item Env:FLOWSTOCK_TEST_STDERR_GATE -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stdoutGate, $stderrGate -Force -ErrorAction SilentlyContinue
}

Write-Host 'deploy transport streaming runtime test passed'

