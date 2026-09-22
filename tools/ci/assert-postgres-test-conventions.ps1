[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $DiscoveredTestsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Test source root does not exist: $SourceRoot"
}
if (-not (Test-Path -LiteralPath $DiscoveredTestsPath -PathType Leaf)) {
    throw "Discovered-tests file does not exist: $DiscoveredTestsPath"
}

$repositoryRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw 'Cannot resolve the repository root.'
}

$resolvedSourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$relativeSourceRoot = [IO.Path]::GetRelativePath($repositoryRoot, $resolvedSourceRoot).Replace('\', '/')
$trackedFiles = @(& git ls-files -- "$relativeSourceRoot/*.cs")
if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) {
    throw "No tracked C# test sources found under: $relativeSourceRoot"
}

$discoveredTests = @(
    Get-Content -LiteralPath $DiscoveredTestsPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^FlowStock\.Server\.Tests\.' }
)
if ($discoveredTests.Count -eq 0) {
    throw 'Test discovery produced no FlowStock.Server.Tests tests.'
}

$signalPatterns = @(
    'FLOWSTOCK_POSTGRES_TEST_CONNECTION',
    'FLOWSTOCK_POSTGRES_CONNECTION',
    'POSTGRES_CONNECTION_STRING',
    '\[\s*Postgres(?:Fact|Theory)(?:Attribute)?\b',
    '\b(?:ResolvePostgresTestConnectionString|ResolveRequiredPostgresTestConnectionString|RequirePostgresTestConnectionString|RequiredConnection)\s*\(',
    '(?m)^\s*using\s+Npgsql\s*;',
    '\bNpgsql\.'
)

$violations = [System.Collections.Generic.List[string]]::new()
$dbAwareClassCount = 0

foreach ($relativePath in $trackedFiles) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    $content = Get-Content -LiteralPath $fullPath -Raw
    $hasSignal = $false

    foreach ($pattern in $signalPatterns) {
        if ($content -match $pattern) {
            $hasSignal = $true
            break
        }
    }

    if (-not $hasSignal -and
        $content -match '\bPostgresDataStore\b' -and
        $content -match '\.Initialize\s*\(') {
        $hasSignal = $true
    }

    if (-not $hasSignal) {
        continue
    }

    $namespaceMatch = [regex]::Match($content, '(?m)^\s*namespace\s+([A-Za-z_][\w.]*)\s*;')
    if (-not $namespaceMatch.Success) {
        $violations.Add("Cannot resolve namespace for DB-aware source: $relativePath")
        continue
    }

    $namespace = $namespaceMatch.Groups[1].Value
    $classMatches = [regex]::Matches(
        $content,
        '(?m)^\s*(?:public|internal)\s+(?:(?:sealed|static|abstract|partial)\s+)*class\s+([A-Za-z_]\w*)')
    $testClasses = [System.Collections.Generic.List[string]]::new()

    foreach ($classMatch in $classMatches) {
        $className = $classMatch.Groups[1].Value
        $classFqn = "$namespace.$className"
        if (@($discoveredTests | Where-Object { $_.StartsWith("$classFqn.", [StringComparison]::Ordinal) }).Count -gt 0) {
            $testClasses.Add($classFqn)
        }
    }

    if ($testClasses.Count -eq 0) {
        $violations.Add("DB-aware source has no discovered test class: $relativePath")
        continue
    }

    foreach ($classFqn in $testClasses) {
        $dbAwareClassCount++
        $className = $classFqn.Substring($classFqn.LastIndexOf('.') + 1)
        if (-not $className.Contains('Postgres', [StringComparison]::Ordinal)) {
            $violations.Add("DB-aware test class must contain 'Postgres' in its name: $classFqn ($relativePath)")
            continue
        }

        $classTests = @($discoveredTests | Where-Object { $_.StartsWith("$classFqn.", [StringComparison]::Ordinal) })
        if ($classTests.Count -eq 0) {
            $violations.Add("DB-aware test class has no discovered tests: $classFqn")
        }
        foreach ($testName in $classTests) {
            if (-not $testName.Contains('Postgres', [StringComparison]::Ordinal)) {
                $violations.Add("DB-aware test is not selected by FullyQualifiedName~Postgres: $testName")
            }
        }
    }
}

if ($dbAwareClassCount -eq 0) {
    $violations.Add('No DB-aware test classes were detected; the convention guard cannot protect the filter.')
}

if ($violations.Count -gt 0) {
    throw "PostgreSQL test convention gate failed:`n$($violations -join "`n")"
}

Write-Host "PostgreSQL test convention gate passed: dbAwareClasses=$dbAwareClassCount, discoveredTests=$($discoveredTests.Count)."
