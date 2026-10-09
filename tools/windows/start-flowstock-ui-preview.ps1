[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),
    [string] $DotnetExecutable = 'dotnet',
    [switch] $DisableStartupSplash
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $RepositoryRoot 'apps\windows\FlowStock.App\FlowStock.App.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "FlowStock.App project not found: $project"
}

$startupStatusPath = $null
$previousStatusPath = $env:FLOWSTOCK_STARTUP_STATUS_FILE

function Write-StartupStatus(
    [string] $State,
    [string] $Message,
    [Nullable[int]] $ProcessId = $null
) {
    if ([string]::IsNullOrWhiteSpace($startupStatusPath)) {
        return
    }

    $payload = [ordered]@{
        state = $State
        message = $Message
        logPath = (Join-Path ([IO.Path]::GetTempPath()) 'FlowStock-UiPreview\app.log')
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($null -ne $ProcessId) {
        $payload.processId = $ProcessId.Value
    }

    $temporary = "$startupStatusPath.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText(
        $temporary,
        ($payload | ConvertTo-Json -Compress),
        [Text.Encoding]::UTF8)
    Move-Item -LiteralPath $temporary -Destination $startupStatusPath -Force
}

if (-not $DisableStartupSplash) {
    $splashScript = Join-Path $PSScriptRoot 'show-flowstock-startup-splash.ps1'
    if (Test-Path -LiteralPath $splashScript -PathType Leaf) {
        $startupDirectory = Join-Path ([IO.Path]::GetTempPath()) 'FlowStock-Startup'
        New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
        $startupStatusPath = Join-Path $startupDirectory "$([Guid]::NewGuid().ToString('N')).json"
        Write-StartupStatus 'starting' 'Сборка и запуск UI Preview / DEV…'
        $splashArguments = @(
            '-NoLogo',
            '-NoProfile',
            '-STA',
            '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $splashScript + '"'),
            '-StatusPath', ('"' + $startupStatusPath + '"'),
            '-TimeoutSeconds', '120'
        )
        Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList $splashArguments | Out-Null
        $env:FLOWSTOCK_STARTUP_STATUS_FILE = $startupStatusPath
    }
}

try {
    $arguments = @('run', '--project', $project, '--', '--ui-preview')
    $process = Start-Process -FilePath $DotnetExecutable -WorkingDirectory $RepositoryRoot -ArgumentList $arguments -NoNewWindow -PassThru

    if (-not [string]::IsNullOrWhiteSpace($startupStatusPath)) {
        try {
            $current = Get-Content -LiteralPath $startupStatusPath -Raw | ConvertFrom-Json
            if ($current.state -eq 'starting') {
                Write-StartupStatus 'starting' ([string]$current.message) $process.Id
            }
        } catch {
            # The application may already have atomically replaced the startup status.
        }
    }

    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -and -not [string]::IsNullOrWhiteSpace($startupStatusPath)) {
        $current = $null
        try {
            $current = Get-Content -LiteralPath $startupStatusPath -Raw | ConvertFrom-Json
        } catch {
        }
        if ($null -eq $current -or $current.state -eq 'starting') {
            Write-StartupStatus 'error' "UI Preview / DEV завершился с кодом $($process.ExitCode)."
        }
    }

    exit $process.ExitCode
} catch {
    if (-not [string]::IsNullOrWhiteSpace($startupStatusPath)) {
        Write-StartupStatus 'error' "Не удалось запустить UI Preview / DEV: $($_.Exception.Message)"
    }
    throw
} finally {
    if ($null -eq $previousStatusPath) {
        Remove-Item Env:FLOWSTOCK_STARTUP_STATUS_FILE -ErrorAction SilentlyContinue
    } else {
        $env:FLOWSTOCK_STARTUP_STATUS_FILE = $previousStatusPath
    }
}
