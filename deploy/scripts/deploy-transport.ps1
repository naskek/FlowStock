Set-StrictMode -Version Latest

function Invoke-ProcessWithRawStdin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [byte[]]$StdinBytes,

        [scriptblock]$StdOutHandler,

        [scriptblock]$StdErrHandler
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = $encoding
    $startInfo.StandardErrorEncoding = $encoding

    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false

    try {
        if (-not $process.Start()) {
            throw "Failed to start process: $FilePath"
        }
        $started = $true

        $stdoutCapture = [System.Text.StringBuilder]::new()
        $stderrCapture = [System.Text.StringBuilder]::new()
        $stdoutBuffer = [char[]]::new(4096)
        $stderrBuffer = [char[]]::new(4096)
        $stdoutClosed = $false
        $stderrClosed = $false
        $stdoutRead = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrRead = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)

        try {
            $process.StandardInput.BaseStream.Write($StdinBytes, 0, $StdinBytes.Length)
            $process.StandardInput.BaseStream.Flush()
        }
        finally {
            $process.StandardInput.Close()
        }

        while (-not ($stdoutClosed -and $stderrClosed -and $process.HasExited)) {
            $madeProgress = $false

            if (-not $stdoutClosed -and $stdoutRead.IsCompleted) {
                $count = $stdoutRead.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stdoutClosed = $true
                }
                else {
                    $chunk = [string]::new($stdoutBuffer, 0, $count)
                    [void]$stdoutCapture.Append($chunk)
                    if ($null -ne $StdOutHandler) {
                        & $StdOutHandler $chunk
                    }
                    $stdoutRead = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
                }
                $madeProgress = $true
            }

            if (-not $stderrClosed -and $stderrRead.IsCompleted) {
                $count = $stderrRead.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stderrClosed = $true
                }
                else {
                    $chunk = [string]::new($stderrBuffer, 0, $count)
                    [void]$stderrCapture.Append($chunk)
                    if ($null -ne $StdErrHandler) {
                        & $StdErrHandler $chunk
                    }
                    $stderrRead = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
                }
                $madeProgress = $true
            }

            if (-not $madeProgress) {
                Start-Sleep -Milliseconds 10
            }
        }

        $process.WaitForExit()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdoutCapture.ToString()
            StdErr = $stderrCapture.ToString()
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            try {
                $process.Kill($true)
            }
            catch {
                # Best effort only; preserve the original transport failure.
            }
        }
        $process.Dispose()
    }
}

function Invoke-RemoteBashScriptViaSsh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$SshTarget,

        [Parameter(Mandatory = $true)]
        [byte[]]$ScriptBytes,

        [string[]]$RemoteArgumentList = @(),

        [scriptblock]$StdOutHandler,

        [scriptblock]$StdErrHandler
    )

    $remoteScriptPath = "/tmp/flowstock-deploy-$([Guid]::NewGuid().ToString('N')).sh"
    foreach ($argument in $RemoteArgumentList) {
        if ($argument -notmatch '^[A-Za-z0-9._:+/-]+$') {
            throw "Unsafe remote bash argument: $argument"
        }
    }

    $quotedArguments = @($RemoteArgumentList | ForEach-Object { "'$_'" })
    $argumentSuffix = if ($quotedArguments.Count -eq 0) { '' } else { ' ' + ($quotedArguments -join ' ') }

    # stdin is consumed completely by cat before bash starts reading the file.
    # Therefore nested commands in the deploy script cannot drain the script source.
    $remoteCommand = "umask 077; trap 'rm -f $remoteScriptPath' EXIT; cat > '$remoteScriptPath' || exit; bash '$remoteScriptPath'$argumentSuffix"

    return Invoke-ProcessWithRawStdin -FilePath 'ssh' -ArgumentList @($SshTarget, $remoteCommand) -StdinBytes $ScriptBytes -StdOutHandler $StdOutHandler -StdErrHandler $StdErrHandler
}
