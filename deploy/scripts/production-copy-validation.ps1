function Assert-ProductionCopyValidationRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$RecordPath,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-f]{40}$')]
        [string]$ExpectedTree
    )

    if (-not (Test-Path -LiteralPath $RecordPath -PathType Leaf)) {
        throw "Production-copy validation record is missing: $RecordPath"
    }

    try {
        $record = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Production-copy validation record is malformed JSON: $RecordPath"
    }

    $requiredProperties = @(
        'schema_version',
        'validation_kind',
        'result',
        'validated_commit',
        'validated_tree',
        'validated_at_utc'
    )
    foreach ($property in $requiredProperties) {
        if ($record.PSObject.Properties.Name -notcontains $property) {
            throw "Production-copy validation record is missing required field: $property"
        }
    }

    if ([int]$record.schema_version -ne 1) {
        throw 'Production-copy validation record has unsupported schema_version'
    }
    if ([string]$record.validation_kind -ne 'production-copy-manual-smoke') {
        throw 'Production-copy validation record has unexpected validation_kind'
    }
    if ([string]$record.result -ne 'passed') {
        throw 'Production-copy validation record does not report a passed result'
    }

    $validatedCommit = ([string]$record.validated_commit).Trim().ToLowerInvariant()
    $validatedTree = ([string]$record.validated_tree).Trim().ToLowerInvariant()
    $expectedTreeNormalized = $ExpectedTree.Trim().ToLowerInvariant()

    if ($validatedCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Production-copy validation record has invalid validated_commit'
    }
    if ($validatedTree -notmatch '^[0-9a-f]{40}$') {
        throw 'Production-copy validation record has invalid validated_tree'
    }
    if ($validatedTree -ne $expectedTreeNormalized) {
        throw "Production-copy validation tree mismatch: validated=$validatedTree expected=$expectedTreeNormalized"
    }

    $validatedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        [string]$record.validated_at_utc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$validatedAt
    )) {
        throw 'Production-copy validation record has invalid validated_at_utc'
    }
    if ($validatedAt.Offset -ne [TimeSpan]::Zero) {
        throw 'Production-copy validation timestamp must be UTC'
    }

    return $record
}
