<#
.SYNOPSIS
    Installs the repository's committed git hooks.

.DESCRIPTION
    Points core.hooksPath at .githooks so the pre-push guard that blocks direct pushes to `main`
    is active. Constitution Art. VIII.1 requires this whenever the hosting plan cannot enforce
    branch protection (GitHub Free on a private repository). See
    docs/operations/repository-settings.md.

    Run once per clone. Idempotent.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File ./scripts/install-git-hooks.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (-not (Test-Path (Join-Path $repoRoot '.githooks\pre-push'))) {
        throw 'Missing .githooks/pre-push — is this the repository root?'
    }

    git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'git config core.hooksPath failed' }

    Write-Host 'Installed: core.hooksPath = .githooks'
    Write-Host 'Active guard: pre-push refuses direct pushes to: main'
    Write-Host ''
    Write-Host 'Verify with:'
    Write-Host '  git config --get core.hooksPath'
    Write-Host '  git push origin main          # should be refused'
}
finally {
    Pop-Location
}
