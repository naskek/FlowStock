[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),
    [string] $DotnetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $RepositoryRoot 'apps\windows\FlowStock.App\FlowStock.App.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "FlowStock.App project not found: $project"
}

# Source-only UI preview bypasses production launcher, recovery and runtime state.
& $DotnetExecutable run --project $project -- --ui-preview
exit $LASTEXITCODE
