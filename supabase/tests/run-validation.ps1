<#
.SYNOPSIS
    Reproduces the Phase 4 database validation in a disposable local PostgreSQL container.

.DESCRIPTION
    Builds the schema from scratch, applies every migration in order, applies the seed, runs the
    structural verification and both test suites, then rebuilds to prove the schema is
    reproducible.

    No Supabase CLI is required and no Supabase project is touched. The container uses trust
    authentication on a throwaway database, so no password, token or connection string appears in
    this script or in source control.

.EXAMPLE
    pwsh ./supabase/tests/run-validation.ps1
#>

param(
    [string] $ContainerName = 'cvc-verify',
    [string] $Database      = 'convenciones',
    [string] $Image         = 'postgres:17-alpine',
    [int]    $Port          = 55432
)

# Native commands (docker, psql) write progress and NOTICEs to stderr, which PowerShell surfaces as
# error records. So the preference stays Continue and every call checks $LASTEXITCODE explicitly.
$ErrorActionPreference = 'Continue'
$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$supabase  = Join-Path $repoRoot 'supabase'
$failures  = 0

function Invoke-Sql {
    param([string] $Sql)
    $result = docker exec $ContainerName psql -U postgres -d $Database -v ON_ERROR_STOP=1 -tA -q -c $Sql 2>&1 |
              ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $Sql`n$($result -join "`n")" }
    $result
}

function Invoke-SqlFile {
    param([string] $Path)
    $result = docker exec $ContainerName psql -U postgres -d $Database -v ON_ERROR_STOP=1 -f "/supabase/$Path" 2>&1 |
              ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "Script failed: $Path`n$($result -join "`n")" }
    $result
}

Write-Host '=== 1. disposable container ===' -ForegroundColor Cyan
docker rm -f $ContainerName *> $null
docker run --rm -d --name $ContainerName `
    -e POSTGRES_HOST_AUTH_METHOD=trust `
    -e POSTGRES_DB=$Database `
    -p "${Port}:5432" $Image *> $null
Start-Sleep -Seconds 6
docker exec $ContainerName psql -U postgres -d $Database -tAc 'select version();'

function Copy-Sources {
    docker exec $ContainerName sh -c 'rm -rf /supabase' *> $null
    docker cp $supabase "$($ContainerName):/supabase" *> $null
}

function New-CleanDatabase {
    docker exec $ContainerName psql -U postgres -d postgres -q `
        -c "drop database if exists $Database;" -c "create database $Database;" *> $null
}

function Invoke-AllMigrations {
    $applied = 0
    foreach ($m in Get-ChildItem (Join-Path $supabase 'migrations') -Filter *.sql | Sort-Object Name) {
        docker exec $ContainerName psql -U postgres -d $Database -v ON_ERROR_STOP=1 -q `
            -f "/supabase/migrations/$($m.Name)" *> $null
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($m.Name)" }
        Write-Host "  applied $($m.Name)"
        $applied++
    }
    Write-Host "  $applied migrations applied" -ForegroundColor Green
}

Write-Host ''
Write-Host '=== 2. build from scratch ===' -ForegroundColor Cyan
Copy-Sources
New-CleanDatabase
Invoke-AllMigrations
Write-Host '  applying seed.sql'
Invoke-SqlFile 'seed.sql' | Out-Null

Write-Host ''
Write-Host '=== 3. structural verification ===' -ForegroundColor Cyan
Invoke-SqlFile 'tests/verify.sql' | Out-Null

Write-Host ''
Write-Host '=== 4. constraint and referential integrity tests ===' -ForegroundColor Cyan
$constraintOut = Invoke-SqlFile 'tests/constraint-tests.sql'
$constraintPass = @($constraintOut | Select-String -Pattern 'PASS:').Count
$constraintFail = @($constraintOut | Select-String -Pattern 'FAIL:').Count
Write-Host "  PASS=$constraintPass FAIL=$constraintFail"
$failures += $constraintFail

Write-Host ''
Write-Host '=== 5. RLS and authorisation tests ===' -ForegroundColor Cyan
$rlsOut = Invoke-SqlFile 'tests/rls-tests.sql'
$rlsPass = @($rlsOut | Select-String -Pattern 'PASS:').Count
$rlsFail = @($rlsOut | Select-String -Pattern 'FAIL:').Count
Write-Host "  PASS=$rlsPass FAIL=$rlsFail"
$failures += $rlsFail

Write-Host ''
Write-Host '=== 6. no legacy data imported ===' -ForegroundColor Cyan
$rows = Invoke-Sql @"
select coalesce(sum(n), 0) from (
  select 1 as n from app.company
  union all select 1 from app.agreement
  union all select 1 from app.agreement_clause
  union all select 1 from app.negotiation_case
  union all select 1 from app.statute
  union all select 1 from app.news_article
  union all select 1 from platform.user_account
) t;
"@
Write-Host "  business and identity rows present: $($rows.Trim())  (expected 0)"

Write-Host ''
Write-Host '=== 7. reproducibility ===' -ForegroundColor Cyan
$fp1 = docker exec $ContainerName sh -c "pg_dump -U postgres -d $Database --schema-only -n app -n platform" 2>&1
New-CleanDatabase
Invoke-AllMigrations | Out-Null
Invoke-SqlFile 'seed.sql' | Out-Null
$fp2 = docker exec $ContainerName sh -c "pg_dump -U postgres -d $Database --schema-only -n app -n platform" 2>&1
# pg_dump 17.x emits a random \restrict / \unrestrict token per dump; it is the only difference.
$norm1 = ($fp1 | Where-Object { -not $_.StartsWith('\') }) -join "`n"
$norm2 = ($fp2 | Where-Object { -not $_.StartsWith('\') }) -join "`n"
if ($norm1 -eq $norm2) {
    Write-Host '  reproducible: two independent builds produced an identical schema' -ForegroundColor Green
} else {
    Write-Host '  NOT reproducible: the two schema dumps differ' -ForegroundColor Red
    $failures++
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'VALIDATION PASSED' -ForegroundColor Green
} else {
    Write-Host "VALIDATION FAILED - $failures failing assertion(s)" -ForegroundColor Red
    exit 1
}
