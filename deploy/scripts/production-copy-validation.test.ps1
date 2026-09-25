$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'production-copy-validation.ps1')

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("flowstock-prod-copy-gate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Assert-ThrowsContaining {
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Action,
        [Parameter(Mandatory = $true)] [string]$ExpectedText
    )

    $threw = $false
    try {
        & $Action
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike "*$ExpectedText*") {
            throw "Expected error containing '$ExpectedText', got: $($_.Exception.Message)"
        }
    }

    if (-not $threw) {
        throw "Expected action to throw an error containing '$ExpectedText'"
    }
}

function Write-Record {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Tree,
        [string]$Result = 'passed'
    )

    [ordered]@{
        schema_version = 1
        validation_kind = 'production-copy-manual-smoke'
        result = $Result
        validated_commit = ('a' * 40)
        validated_tree = $Tree
        validated_at_utc = '2026-09-25T10:00:00.000Z'
    } | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

try {
    $expectedTree = 'b' * 40
    $recordPath = Join-Path $tempRoot 'validation.json'

    Assert-ThrowsContaining -ExpectedText 'record is missing' -Action {
        Assert-ProductionCopyValidationRecord -RecordPath $recordPath -ExpectedTree $expectedTree | Out-Null
    }

    Set-Content -LiteralPath $recordPath -Value '{not-json' -Encoding utf8NoBOM
    Assert-ThrowsContaining -ExpectedText 'malformed JSON' -Action {
        Assert-ProductionCopyValidationRecord -RecordPath $recordPath -ExpectedTree $expectedTree | Out-Null
    }

    Write-Record -Path $recordPath -Tree ('c' * 40)
    Assert-ThrowsContaining -ExpectedText 'tree mismatch' -Action {
        Assert-ProductionCopyValidationRecord -RecordPath $recordPath -ExpectedTree $expectedTree | Out-Null
    }

    Write-Record -Path $recordPath -Tree $expectedTree -Result 'failed'
    Assert-ThrowsContaining -ExpectedText 'does not report a passed result' -Action {
        Assert-ProductionCopyValidationRecord -RecordPath $recordPath -ExpectedTree $expectedTree | Out-Null
    }

    Write-Record -Path $recordPath -Tree $expectedTree
    $record = Assert-ProductionCopyValidationRecord -RecordPath $recordPath -ExpectedTree $expectedTree
    if ($record.validated_tree -ne $expectedTree) {
        throw 'Validated tree was not returned unchanged'
    }

    Write-Host 'production-copy validation gate tests passed'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
