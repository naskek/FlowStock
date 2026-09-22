[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [switch] $RequireAnyTests,

    [string[]] $RequiredTestClasses = @(),

    [switch] $RequireOnlyPassed,

    [string[]] $AllowedSkippedTests = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "TRX results directory does not exist: $Path"
}

$trxFiles = @(Get-ChildItem -LiteralPath $Path -Filter '*.trx' -File -Recurse)
if ($trxFiles.Count -eq 0) {
    throw "No TRX files found under: $Path"
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($trxFile in $trxFiles) {
    [xml] $document = Get-Content -LiteralPath $trxFile.FullName -Raw
    $testsById = @{}

    foreach ($unitTest in @($document.SelectNodes("//*[local-name()='UnitTest']"))) {
        $testId = [string] $unitTest.GetAttribute('id')
        $testMethod = $unitTest.SelectSingleNode("./*[local-name()='TestMethod']")
        if ([string]::IsNullOrWhiteSpace($testId) -or $null -eq $testMethod) {
            continue
        }

        $className = [string] $testMethod.GetAttribute('className')
        $methodName = [string] $testMethod.GetAttribute('name')
        if ([string]::IsNullOrWhiteSpace($className) -or [string]::IsNullOrWhiteSpace($methodName)) {
            continue
        }

        $testsById[$testId] = [pscustomobject]@{
            ClassName = $className
            TestName = "$className.$methodName"
        }
    }

    foreach ($result in @($document.SelectNodes("//*[local-name()='UnitTestResult']"))) {
        $testId = [string] $result.GetAttribute('testId')
        if ([string]::IsNullOrWhiteSpace($testId) -or -not $testsById.ContainsKey($testId)) {
            throw "Cannot map TRX result '$($result.GetAttribute('testName'))' to a test definition in $($trxFile.FullName)."
        }

        $definition = $testsById[$testId]
        $results.Add([pscustomobject]@{
            ClassName = $definition.ClassName
            TestName = $definition.TestName
            Outcome = [string] $result.GetAttribute('outcome')
            TrxFile = $trxFile.FullName
        })
    }
}

if ($results.Count -eq 0) {
    throw "TRX files under '$Path' contain no test results."
}

if ($RequireAnyTests -and $results.Count -eq 0) {
    throw "At least one test result is required."
}

$allowedSkipped = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] $AllowedSkippedTests,
    [StringComparer]::Ordinal)

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($result in $results) {
    switch ($result.Outcome) {
        'Passed' { }
        'NotExecuted' {
            if ($RequireOnlyPassed -or -not $allowedSkipped.Contains($result.TestName)) {
                $failures.Add("$($result.Outcome): $($result.TestName)")
            }
        }
        default {
            $failures.Add("$($result.Outcome): $($result.TestName)")
        }
    }
}

foreach ($requiredClass in $RequiredTestClasses) {
    $classResults = @($results | Where-Object { $_.ClassName -ceq $requiredClass })
    if ($classResults.Count -eq 0) {
        $failures.Add("Required test class has no executed results: $requiredClass")
    }
}

if ($failures.Count -gt 0) {
    throw "TRX gate failed:`n$($failures -join "`n")"
}

$passedCount = @($results | Where-Object Outcome -eq 'Passed').Count
$skippedCount = @($results | Where-Object Outcome -eq 'NotExecuted').Count
Write-Host "TRX gate passed: total=$($results.Count), passed=$passedCount, allowedSkipped=$skippedCount, files=$($trxFiles.Count)."
