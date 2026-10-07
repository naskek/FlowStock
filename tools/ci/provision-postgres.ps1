[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,

    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 65535)]
    [int]$Port,

    [Parameter(Mandatory = $false)]
    [string]$CacheHit = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$postgresVersion = "16.15"
$packageRevision = "5"
$archiveName = "postgresql-$postgresVersion-$packageRevision-windows-x64-binaries.zip"
$archiveUrl = "https://get.enterprisedb.com/postgresql/$archiveName"
$archiveSha256 = "43BB45F173A6F08CF1D29A97A6D8DEB119E8E8093A24C00D2D1001A0CCAA8281"
$pgsqlRoot = Join-Path $InstallRoot "pgsql"
$binRoot = Join-Path $pgsqlRoot "bin"
$requiredPaths = @(
    (Join-Path $binRoot "postgres.exe"),
    (Join-Path $binRoot "initdb.exe"),
    (Join-Path $binRoot "pg_ctl.exe"),
    (Join-Path $binRoot "createdb.exe"),
    (Join-Path $binRoot "psql.exe"),
    (Join-Path $pgsqlRoot "share")
)

function Assert-PortablePostgres {
    foreach ($path in $requiredPaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Portable PostgreSQL cache is incomplete: missing $path"
        }
    }

    $versionText = & (Join-Path $binRoot "postgres.exe") --version
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot execute cached PostgreSQL server binary."
    }

    $expectedPrefix = "postgres (PostgreSQL) $postgresVersion"
    if (-not ([string]$versionText).StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        throw "Expected portable PostgreSQL $postgresVersion, got: $versionText"
    }
}

$startedAt = Get-Date
$exactCacheHit = $CacheHit -eq "true"

if ($exactCacheHit) {
    Write-Host "Using exact PostgreSQL binary cache."
    Assert-PortablePostgres
}
else {
    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null

    $archivePath = Join-Path $env:RUNNER_TEMP $archiveName
    Write-Host "Downloading pinned PostgreSQL archive: $archiveUrl"
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not $actualSha256.Equals($archiveSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PostgreSQL archive checksum mismatch. Expected $archiveSha256, got $actualSha256"
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $InstallRoot
    Remove-Item -LiteralPath $archivePath -Force
    Assert-PortablePostgres
}

$env:PATH = "$binRoot;$env:PATH"
if ($env:GITHUB_PATH) {
    $binRoot | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
}

foreach ($name in "PGUSER", "PGPASSWORD", "PGDATABASE") {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment variable is missing: $name"
    }
}

if (Test-Path -LiteralPath $DataRoot) {
    Remove-Item -LiteralPath $DataRoot -Recurse -Force
}

$passwordFile = Join-Path $env:RUNNER_TEMP "postgresql-superuser-password.txt"
Set-Content -LiteralPath $passwordFile -Value $env:PGPASSWORD -Encoding ascii -NoNewline

$initdb = Join-Path $binRoot "initdb.exe"
& $initdb `
    --pgdata="$DataRoot" `
    --username="$env:PGUSER" `
    --pwfile="$passwordFile" `
    --auth=scram-sha-256 `
    --encoding=UTF-8 `
    --locale=en_US.UTF-8 `
    --no-instructions
if ($LASTEXITCODE -ne 0) {
    throw "initdb failed with exit code $LASTEXITCODE."
}
Remove-Item -LiteralPath $passwordFile -Force

$logPath = Join-Path $env:RUNNER_TEMP "postgresql-16.log"
$pgCtl = Join-Path $binRoot "pg_ctl.exe"
& $pgCtl -D $DataRoot -l $logPath -o "-p $Port -h 127.0.0.1" -w -t 30 start
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath | Write-Error
    }
    throw "PostgreSQL startup failed with exit code $LASTEXITCODE."
}

$env:PGHOST = "127.0.0.1"
$env:PGPORT = [string]$Port
$env:PGCONNECT_TIMEOUT = "10"

$createdb = Join-Path $binRoot "createdb.exe"
& $createdb --no-password --host=$env:PGHOST --port=$env:PGPORT --username=$env:PGUSER $env:PGDATABASE
if ($LASTEXITCODE -ne 0) {
    throw "Database creation failed with exit code $LASTEXITCODE."
}

$psql = Join-Path $binRoot "psql.exe"
$versionText = & $psql --no-password -At -v ON_ERROR_STOP=1 -d $env:PGDATABASE -c "SHOW server_version_num"
if ($LASTEXITCODE -ne 0) {
    throw "Cannot query provisioned PostgreSQL server version."
}

$serverVersion = 0
if (-not [int]::TryParse($versionText.Trim(), [ref]$serverVersion) -or
    $serverVersion -lt 160000 -or $serverVersion -ge 170000) {
    throw "Expected PostgreSQL 16, got server_version_num=$serverVersion"
}

$elapsed = (Get-Date) - $startedAt
Write-Host ("PostgreSQL 16 provisioning completed in {0:N1}s (cache-hit={1})." -f $elapsed.TotalSeconds, $exactCacheHit)
