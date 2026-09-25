[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [switch]$ConfirmPassed,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ConfirmPassed.IsPresent) {
    throw 'Explicit -ConfirmPassed is required after the manual production-copy smoke succeeds.'
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Required command is not installed: git'
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'Unable to resolve repository root'
}

Push-Location $repoRoot
try {
    if (@(& git status --porcelain).Count -ne 0) {
        throw 'Worktree must be clean before recording production-copy validation'
    }

    $commit = (& git rev-parse --verify 'HEAD^{commit}').Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve validated commit'
    }

    $tree = (& git rev-parse --verify 'HEAD^{tree}').Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve validated tree'
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path $repoRoot '.local/production-copy-validation.json'
    }
    elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath = Join-Path $repoRoot $OutputPath
    }

    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $record = [ordered]@{
        schema_version = 1
        validation_kind = 'production-copy-manual-smoke'
        result = 'passed'
        validated_commit = $commit
        validated_tree = $tree
        validated_at_utc = [DateTime]::UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            [Globalization.CultureInfo]::InvariantCulture
        )
    }

    $record | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
    Write-Host "Production-copy validation recorded: commit=$commit tree=$tree"
    Write-Host "Record: $OutputPath"
}
finally {
    Pop-Location
}
