<#
.SYNOPSIS
    Scans the repository for credential-shaped strings and fails when it finds one.

.DESCRIPTION
    Reads the tracked files (git ls-files) and reports any line that looks like a committed credential:
    a credential-named assignment with a quoted literal, a URL that carries a password, a legacy
    mysql_connect with a literal password, a private-key block, or an AWS access-key id.

    It prints file:line and the pattern that matched, with any quoted value redacted: a scan that
    echoes the secret defeats its own purpose. Exit code 0 means clean, 1 means at least one match.

    This is the check T16 AC 1 asks for and the one T18 AC 5 will run against the built bundle. It is
    deliberately small and dependency-free so CI can run it without a toolchain.

.PARAMETER IncludeWorkingTree
    Also scan untracked files that are not ignored. The default is the tracked set, which is what the
    repository actually contains; untracked developer files (node_modules/, build output) are out of scope.

.EXAMPLE
    pwsh ./tools/scan-secrets.ps1
#>
param(
    [switch] $IncludeWorkingTree,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'

# These markers are assembled at run time so this file does not contain the very strings the patterns
# look for. A scanner that trips its own rule is worse than useless.
$privateKeyMarker = '-----BEGIN ' + '[A-Z ]*PRIVATE KEY-----'
$schemeSeparator = ':' + '/' + '/'

$patterns = @(
    @{
        Name    = 'credential-assignment'
        Note    = 'a credential-named field assigned a quoted literal'
        Pattern = '(?i)\b(?:password|passwd|pwd|clave|secret|api[_-]?key|apikey|access[_-]?token|private[_-]?key)\b\s*[:=]\s*(?:"[^"]{3,}"|''[^'']{3,}'')'
    },
    @{
        Name    = 'connection-url-with-password'
        Note    = 'a URL that embeds a user password'
        Pattern = '(?i)\b[a-z][a-z0-9+.-]*' + $schemeSeparator + '[^\s:/@]+:' + '[^\s@/]+@'
    },
    @{
        Name    = 'legacy-db-connect'
        Note    = 'mysql_connect with a literal password argument'
        Pattern = 'mysql_connect\s*\([^)]*,\s*[^,)]*,\s*(?:"[^"]+"|''[^'']+'')'
    },
    @{
        Name    = 'private-key-block'
        Note    = 'a PEM private key'
        Pattern = $privateKeyMarker
    },
    @{
        Name    = 'aws-access-key-id'
        Note    = 'an AWS access key id'
        Pattern = '\bAKIA[0-9A-Z]{16}\b'
    }
)

function Get-TargetFiles {
    $tracked = @(git ls-files)
    $files = if ($IncludeWorkingTree) { $tracked + @(git ls-files --others --exclude-standard) } else { $tracked }

    # Text formats only; a binary file cannot carry a readable credential line and would only slow the scan.
    $textExtensions = @('.cs', '.csproj', '.props', '.json', '.md', '.sql', '.ts', '.tsx', '.js', '.mjs',
        '.html', '.css', '.ps1', '.sh', '.yml', '.yaml', '.toml', '.xml', '.slnx', '.txt', '.example', '.config')

    $files | Where-Object {
        $extension = [System.IO.Path]::GetExtension($_)
        ($textExtensions -contains $extension) -or ($extension -eq '') -or ([System.IO.Path]::GetFileName($_) -eq '.gitignore') -or ([System.IO.Path]::GetFileName($_) -eq '.gitattributes')
    } | Sort-Object -Unique
}

function Get-RedactedLine {
    param([string] $Line)

    $redacted = $Line -replace '(?<=[:=]\s*)("[^"]{3,}"|''[^'']{3,}'')', '<redacted>'
    $redacted = $redacted -replace '(?i)(://[^\s:/@]+:)[^\s@/]+@', '$1<redacted>@'

    if ($redacted.Length -gt 140) {
        $redacted = $redacted.Substring(0, 140) + '...'
    }

    $redacted.Trim()
}

$files = @(Get-TargetFiles)
$findings = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    # Resolve and stat through .NET rather than Get-Item: the PowerShell provider behaves
    # differently for bare dotfiles on Linux, which is where CI runs.
    $resolved = Resolve-Path -LiteralPath $file -ErrorAction SilentlyContinue
    if (-not $resolved) {
        continue
    }

    $path = $resolved.Path
    if ([System.IO.FileInfo]::new($path).Length -gt 2MB) {
        if (-not $Quiet) { Write-Host "skipped (larger than 2 MB): $file" -ForegroundColor DarkGray }
        continue
    }

    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $lineNumber = 0

    foreach ($line in ($text -split "`r?`n")) {
        $lineNumber += 1

        foreach ($rule in $patterns) {
            if ($line -match $rule.Pattern) {
                $findings.Add([pscustomobject]@{
                    File    = $file
                    Line    = $lineNumber
                    Rule    = $rule.Name
                    Note    = $rule.Note
                    Preview = (Get-RedactedLine -Line $line)
                })
                break
            }
        }
    }
}

Write-Host ''
Write-Host "Secret scan - $($files.Count) files, $($patterns.Count) patterns" -ForegroundColor Cyan

if ($findings.Count -eq 0) {
    Write-Host 'CLEAN: no credential-shaped string found.' -ForegroundColor Green
    Write-Host ''
    exit 0
}

Write-Host "FAILED: $($findings.Count) credential-shaped match(es)." -ForegroundColor Red
foreach ($match in $findings) {
    Write-Host ("  {0}:{1}  [{2}] {3}" -f $match.File, $match.Line, $match.Rule, $match.Note)
    if (-not $Quiet) {
        Write-Host ("      {0}" -f $match.Preview) -ForegroundColor DarkGray
    }
}

Write-Host ''
exit 1
