[CmdletBinding()]
param(
    [string] $RepositoryRoot = 'D:\FlowStock',
    [string] $DotnetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'

$desktopRoot = Join-Path $env:LOCALAPPDATA 'FlowStock\Desktop'
$stateRoot = Join-Path $desktopRoot 'state'
$pendingPath = Join-Path $stateRoot 'pending-transaction.json'
$activePath = Join-Path $stateRoot 'active-runtime.json'
$lkgPath = Join-Path $stateRoot 'last-known-good.json'
$sourceProject = Join-Path $RepositoryRoot 'apps\windows\FlowStock.App\FlowStock.App.csproj'
$emergencyLog = Join-Path $env:APPDATA 'FlowStock\Logs\Updates\launcher-emergency.log'
$shaPattern = '^[0-9a-f]{40}$'
$sessionPattern = '^[0-9a-f]{32}$'

function Write-EmergencyDiagnostic([string] $Message) {
    $directory = Split-Path -Parent $emergencyLog
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Add-Content -LiteralPath $emergencyLog -Value "$(Get-Date -Format o) $Message"
}

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        Write-EmergencyDiagnostic "Invalid state file rejected: $Path"
        return $null
    }
}

function Get-RuntimeExecutable($Manifest, [string] $Kind) {
    if ($null -eq $Manifest -or $Manifest.schemaVersion -ne 1 -or $Manifest.commit -notmatch $shaPattern) {
        return $null
    }

    $directory = if ($Kind -eq 'app') { 'app' } else { 'updater' }
    $file = if ($Kind -eq 'app') { 'FlowStock.App.exe' } else { 'FlowStock.Updater.exe' }
    $path = Join-Path $desktopRoot "versions\$($Manifest.commit)\$directory\$file"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
        if ($productVersion -ne "$($Manifest.productVersion)+$($Manifest.commit)") { return $null }
        return $path
    } catch {
        return $null
    }
}

function Get-BundleSha256([string] $Directory) {
    $lines = Get-ChildItem -LiteralPath $Directory -File -Recurse |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/').ToLowerInvariant()
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relative`:$hash`n"
        } |
        Sort-Object -CaseSensitive
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join ''))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Start-RecoveryIfPending {
    $pending = Read-JsonFile $pendingPath
    if ($null -eq $pending) { return $false }
    if ($pending.schemaVersion -ne 1 -or $pending.sessionId -notmatch $sessionPattern) {
        Write-EmergencyDiagnostic 'Rejected invalid pending transaction.'
        return $true
    }
    if ($pending.phase -in @('fallback-ready', 'success-ready')) {
        return $true
    }

    $recoveryDirectory = Join-Path $desktopRoot "transactions\$($pending.sessionId)\recovery"
    $recoveryExecutable = Join-Path $recoveryDirectory 'FlowStock.Updater.exe'
    if ((Test-Path -LiteralPath $recoveryExecutable -PathType Leaf) -and
        (Get-BundleSha256 $recoveryDirectory) -eq $pending.recoveryBundleSha256) {
        $process = Start-Process -FilePath $recoveryExecutable -WorkingDirectory $recoveryDirectory `
            -ArgumentList @('--recover', $pending.sessionId) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Recovery updater exited with code $($process.ExitCode)." }
        return $true
    }

    $lkg = Read-JsonFile $lkgPath
    $lkgUpdater = Get-RuntimeExecutable $lkg 'updater'
    if ($null -ne $lkgUpdater) {
        Write-EmergencyDiagnostic 'Transaction recovery bundle invalid; using LKG updater.'
        $process = Start-Process -FilePath $lkgUpdater -WorkingDirectory (Split-Path -Parent $lkgUpdater) `
            -ArgumentList @('--recover', $pending.sessionId) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "LKG recovery updater exited with code $($process.ExitCode)." }
        return $true
    }

    Write-EmergencyDiagnostic 'No validated recovery updater is available; unresolved candidate will not be started.'
    return $true
}

function Start-RecoveredRuntimeIfReady {
    $pending = Read-JsonFile $pendingPath
    if ($null -eq $pending -or $pending.phase -notin @('fallback-ready', 'success-ready')) { return $false }

    $arguments = @('--update-session', $pending.sessionId)
    if ($pending.phase -eq 'success-ready') {
        $active = Read-JsonFile $activePath
        if ($null -eq $active -or
            $active.commit -ne $pending.target.sourceCommit -or
            $active.productVersion -ne $pending.target.productVersion) {
            throw 'Confirmed active runtime does not match success-ready target.'
        }
        $executable = Get-RuntimeExecutable $active 'app'
        if ($null -eq $executable) { throw 'Confirmed success-ready runtime is not available.' }
        Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) `
            -ArgumentList $arguments
    } elseif ($pending.sourceRunFallback -eq $true) {
        $sourceArguments = @('run', '--project', $sourceProject, '--') + $arguments
        Start-Process -FilePath $DotnetExecutable -WorkingDirectory $RepositoryRoot `
            -ArgumentList $sourceArguments
    } else {
        $active = Read-JsonFile $activePath
        $executable = Get-RuntimeExecutable $active 'app'
        if ($null -eq $executable) {
            $lkg = Read-JsonFile $lkgPath
            $executable = Get-RuntimeExecutable $lkg 'app'
        }
        if ($null -eq $executable) { throw 'Recovered active/LKG runtime is not available.' }
        Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) `
            -ArgumentList $arguments
    }

    Remove-Item -LiteralPath $pendingPath
    return $true
}

try {
    [void](Start-RecoveryIfPending)
} catch {
    Write-EmergencyDiagnostic "Recovery failed: $($_.Exception.Message)"
    exit 1
}

try {
    if (Start-RecoveredRuntimeIfReady) { exit 0 }
} catch {
    Write-EmergencyDiagnostic "Recovered runtime launch failed; pending transaction retained: $($_.Exception.Message)"
    exit 1
}

$active = Read-JsonFile $activePath
$activeExecutable = Get-RuntimeExecutable $active 'app'
if ($null -ne $activeExecutable -and -not (Test-Path -LiteralPath $pendingPath -PathType Leaf)) {
    Start-Process -FilePath $activeExecutable -WorkingDirectory (Split-Path -Parent $activeExecutable)
    exit 0
}

$lkg = Read-JsonFile $lkgPath
$lkgExecutable = Get-RuntimeExecutable $lkg 'app'
if ($null -ne $lkgExecutable) {
    Write-EmergencyDiagnostic 'Active runtime unavailable; starting LKG runtime.'
    Start-Process -FilePath $lkgExecutable -WorkingDirectory (Split-Path -Parent $lkgExecutable)
    exit 0
}

Write-EmergencyDiagnostic 'Active/LKG runtime unavailable; starting source-run bootstrap mode.'
Start-Process -FilePath $DotnetExecutable -WorkingDirectory $RepositoryRoot `
    -ArgumentList @('run', '--project', $sourceProject)
