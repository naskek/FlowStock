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
        [byte[]]$StdinBytes
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

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        try {
            $process.StandardInput.BaseStream.Write($StdinBytes, 0, $StdinBytes.Length)
            $process.StandardInput.BaseStream.Flush()
        }
        finally {
            $process.StandardInput.Close()
        }

        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
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


function ConvertTo-ShellSingleQuotedArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    return "'" + $Value.Replace("'", "'\"'\"'") + "'"
}

function Invoke-RemoteBashScriptViaSsh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$SshTarget,

        [Parameter(Mandatory = $true)]
        [byte[]]$ScriptBytes,

        [string[]]$RemoteArgumentList = @()
    )

    $remoteScriptPath = "/tmp/flowstock-deploy-$([Guid]::NewGuid().ToString('N')).sh"
    $quotedPath = ConvertTo-ShellSingleQuotedArgument $remoteScriptPath
    $quotedArguments = @($RemoteArgumentList | ForEach-Object { ConvertTo-ShellSingleQuotedArgument $_ })
    $argumentSuffix = if ($quotedArguments.Count -eq 0) { '' } else { ' ' + ($quotedArguments -join ' ') }

    # stdin is consumed completely by cat before bash starts reading the file.
    # Therefore nested commands in the deploy script cannot drain the script source.
    $remoteCommand = "umask 077; trap 'rm -f $remoteScriptPath' EXIT; cat > $quotedPath || exit; bash $quotedPath$argumentSuffix"

    return Invoke-ProcessWithRawStdin -FilePath 'ssh' -ArgumentList @($SshTarget, $remoteCommand) -StdinBytes $ScriptBytes
}
