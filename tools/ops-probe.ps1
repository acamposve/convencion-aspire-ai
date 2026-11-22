<#
.SYNOPSIS
    Operational probe for Convenciones: asserts the P1-009 signals and prints a verdict.

.DESCRIPTION
    The probe reads four things and nothing else:

      * the public edge (/health/live, /health/ready, and the /health/ops path that must answer 404);
      * the internal operational report (/health/ops) over the loopback port published by
        deploy/docker-compose.yml on the droplet;
      * the listening sockets bound to that loopback port, so a bind change to 0.0.0.0 is detected;
      * the JSON payload of /health/ops, which carries the queue, worker and database verdicts.

    Severities follow docs/operations/monitoring.md:

      P1  the service is not serving, or internal diagnostics are publicly reachable
      P2  a workflow is degraded: queue backlog or a stale worker heartbeat
      P3  the threshold configuration is missing, so the signal cannot be judged (not an outage)

    Exit code is 1 for P1 and P2, 0 for OK and P3. The verdict is printed as Markdown so the workflow
    can append it to the step summary and to the ops-alert issue.

    No secret is printed: the script reads no credential and calls ssh with host-key pinning.

.PARAMETER SelfTest
    Runs the embedded fixtures through the decision table and asserts the expected severity for each.
    Needs no network, no Docker and no database: it is the part CI can run.

.PARAMETER PublicHost
    Public host name of the deployment (default: the staging sslip name).

.PARAMETER SshTarget
    user@host used to reach the droplet. Required unless -SelfTest is used.

.PARAMETER SummaryPath
    Optional file that receives the Markdown summary (the workflow passes $GITHUB_STEP_SUMMARY).

.PARAMETER IssueBodyPath
    Optional file that receives the Markdown body for the ops-alert issue.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$PublicHost = '138-197-15-40.sslip.io',
    [string]$SshTarget,
    [string]$SummaryPath,
    [string]$IssueBodyPath
)

# A probe must report a failed signal, not die on it: native command errors are captured and mapped to
# '000'/P1 instead of terminating the run (PowerShell 5.1 turns native stderr into a terminating error
# when this is 'Stop').
$ErrorActionPreference = 'Continue'

$LoopbackUrl = 'http://127.0.0.1:8081/health/ops'
$RunbookHint = 'docs/operations/runbook.md'

function New-Signal {
    param([string]$Name, [string]$Status, [string]$Description, $Data)
    [pscustomobject]@{ Name = $Name; Status = $Status; Description = $Description; Data = $Data }
}

<#
    The decision table. Pure: it takes what was observed and returns severity, reasons and the signals,
    so every branch is exercised by -SelfTest instead of only during an incident.
#>
function Get-ProbeVerdict {
    param(
        [string]$LiveCode,
        [string]$ReadyCode,
        [string]$EdgeOpsCode,
        [string]$OpsCode,
        [string]$OpsJson,
        [string]$Listeners
    )

    $findings = New-Object System.Collections.Generic.List[object]
    $signals = New-Object System.Collections.Generic.List[object]

    $add = {
        param([string]$Level, [string]$Message)
        $findings.Add([pscustomobject]@{ Level = $Level; Message = $Message })
    }

    if ($LiveCode -ne '200') { & $add 'P1' "liveness answered $LiveCode; the process is not serving" }
    if ($ReadyCode -ne '200') { & $add 'P1' "readiness answered $ReadyCode; the database is unreachable or the schema is missing" }
    if ($EdgeOpsCode -ne '404') {
        if ($EdgeOpsCode -eq '000') {
            & $add 'P1' 'the edge did not answer for /health/ops; the public host is unreachable'
        } else {
            & $add 'P1' ("the edge answered $EdgeOpsCode for /health/ops; internal diagnostics must not be reachable publicly")
        }
    }

    # $listenerLines, not $listeners: the parameter is a [string], and PowerShell variable names are
    # case-insensitive, so reusing the name would coerce the array back to a string.
    $listenerLines = @($Listeners -split "`n" | Where-Object { $_ -ne '' })
    $loopbackOnly = $listenerLines.Count -eq 1 -and $listenerLines[0].Trim() -eq '127.0.0.1:8081'
    if (-not $loopbackOnly) {
        $observed = if ($listenerLines.Count -eq 0) { 'nothing' } else { $listenerLines -join ', ' }
        & $add 'P1' "port 8081 listeners: $observed; expected only 127.0.0.1:8081"
    }

    if ($OpsCode -ne '200' -and $OpsCode -ne '503') {
        & $add 'P1' "the loopback operational report answered $OpsCode"
    }

    if (-not [string]::IsNullOrWhiteSpace($OpsJson)) {
        try {
            $report = $OpsJson | ConvertFrom-Json
        } catch {
            $report = $null
            & $add 'P1' 'the operational report was not valid JSON'
        }

        if ($null -ne $report) {
            switch ($report.status) {
                'Unhealthy' { & $add 'P1' 'the operational report is Unhealthy' }
                'Unknown' { & $add 'P3' 'the operational report is Unknown: thresholds are not configured' }
                'Healthy' { }
                'Degraded' { }
                default { & $add 'P1' "the operational report returned an unexpected status '$($report.status)'" }
            }

            $expected = @('database', 'queue', 'worker')
            foreach ($check in @($report.checks)) {
                $signals.Add((New-Signal $check.name $check.status $check.description $check.data))

                switch ($check.name) {
                    'database' {
                        if ($check.status -eq 'Unhealthy') { & $add 'P1' 'the database check failed' }
                        if ($check.status -eq 'Unknown') { & $add 'P3' 'the database check is Unknown' }
                    }
                    'queue' {
                        switch ($check.status) {
                            'Unhealthy' { & $add 'P1' "the queue cannot be read: $($check.description)" }
                            'Degraded' { & $add 'P2' $check.description }
                            'Unknown' { & $add 'P3' $check.description }
                        }
                    }
                    'worker' {
                        switch ($check.status) {
                            'Unhealthy' { & $add 'P1' "the worker heartbeat cannot be read: $($check.description)" }
                            'Degraded' { & $add 'P2' $check.description }
                            'Unknown' { & $add 'P3' $check.description }
                        }
                    }
                    default { & $add 'P3' "the report contains an unrecognised check '$($check.name)'" }
                }
            }

            foreach ($name in $expected) {
                if (-not (@($report.checks).name -contains $name)) {
                    & $add 'P1' "the operational report is missing the '$name' check"
                }
            }
        }
    }
    elseif ($OpsCode -eq '200' -or $OpsCode -eq '503') {
        & $add 'P1' 'the operational report answered without a body'
    }

    $severity = 'OK'
    foreach ($finding in $findings) {
        if ($finding.Level -eq 'P1') { $severity = 'P1'; break }
        if ($finding.Level -eq 'P2' -and $severity -ne 'P1') { $severity = 'P2' }
        if ($finding.Level -eq 'P3' -and $severity -eq 'OK') { $severity = 'P3' }
    }

    [pscustomobject]@{
        Severity = $severity
        Findings = $findings
        Signals  = $signals
        ExitCode = if ($severity -eq 'P1' -or $severity -eq 'P2') { 1 } else { 0 }
    }
}

function Format-Verdict {
    param($Verdict, [string]$LiveCode, [string]$ReadyCode, [string]$EdgeOpsCode, [string]$OpsCode, [string]$Listeners)

    $lines = New-Object System.Collections.Generic.List[string]
    $badge = switch ($Verdict.Severity) {
        'OK' { 'healthy' }
        'P3' { 'configuration gap (P3)' }
        'P2' { 'degraded (P2)' }
        'P1' { 'failing (P1)' }
    }

    $lines.Add("### Ops probe - $badge")
    $lines.Add('')

    $queue = $Verdict.Signals | Where-Object { $_.Name -eq 'queue' } | Select-Object -First 1
    $worker = $Verdict.Signals | Where-Object { $_.Name -eq 'worker' } | Select-Object -First 1

    $lines.Add('| Signal | Observed | Expected |')
    $lines.Add('|---|---|---|')
    $lines.Add("| ``/health/live`` | $LiveCode | 200 |")
    $lines.Add("| ``/health/ready`` | $ReadyCode | 200 |")
    $lines.Add("| ``/health/ops`` at the edge | $EdgeOpsCode | 404 |")
    $lines.Add("| ``/health/ops`` on loopback | $OpsCode | 200, or 503 when Unhealthy |")
    $lines.Add("| 8081 listeners | $(if ([string]::IsNullOrWhiteSpace($Listeners)) { 'nothing' } else { ($Listeners -split "`n" | Where-Object { $_ -ne '' }) -join ', ' }) | 127.0.0.1:8081 only |")
    if ($queue) { $lines.Add("| queue | $($queue.Status): $($queue.Description) | Healthy |") }
    if ($worker) { $lines.Add("| worker | $($worker.Status): $($worker.Description) | Healthy |") }
    $lines.Add('')

    if ($Verdict.Findings.Count -eq 0) {
        $lines.Add('No finding: every signal is within its configured threshold.')
    } else {
        $lines.Add('Findings:')
        foreach ($finding in $Verdict.Findings) {
            $lines.Add("- **$($finding.Level)** $($finding.Message)")
        }
    }

    $lines.Add('')
    $lines.Add("First response: see [$RunbookHint]($RunbookHint) - database down (section 3.1), worker stopped (section 3.2), queue backlog (section 3.3), provider outage (section 3.4), failed deployment (section 3.5).")
    ($lines -join [Environment]::NewLine)
}

# Windows PowerShell aliases `curl` to Invoke-WebRequest; resolve the real binary so the same script runs
# on the Windows workstation and on the Ubuntu runner.
function Resolve-Binary([string]$Name) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { throw "The '$Name' command is required." }
    return $command.Source
}

function Invoke-Code([string]$Url, [string]$Curl) {
    $code = & $Curl -s -o /dev/null -w '%{http_code}' --max-time 10 $Url 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($code)) { return '000' }
    return $code.Trim()
}

function Invoke-Remote([string]$Command, [string]$Ssh) {
    $output = & $Ssh -o BatchMode=yes -o StrictHostKeyChecking=yes $SshTarget $Command 2>$null
    [pscustomobject]@{
        Exit   = $LASTEXITCODE
        Output = (@($output) -join [Environment]::NewLine)
    }
}

function Invoke-SelfTest {
    $healthy = '{"status":"Healthy","checkedAt":"2026-11-22T15:00:00Z","checks":[{"name":"database","status":"Healthy","description":"The database answered.","data":{}},{"name":"queue","status":"Healthy","description":"The queue is empty.","data":{"queued":0,"running":0,"oldestAgeSeconds":0,"thresholdSeconds":900,"queueEmpty":true}},{"name":"worker","status":"Healthy","description":"The worker is disabled and its heartbeat is current.","data":{"ageSeconds":12,"thresholdSeconds":120,"enabled":false}}]}'
    $dbDown = '{"status":"Unhealthy","checkedAt":"2026-11-22T15:00:00Z","checks":[{"name":"database","status":"Unhealthy","description":"The database check failed.","data":{}},{"name":"queue","status":"Unhealthy","description":"The queue could not be read: connection refused","data":{"thresholdSeconds":900}},{"name":"worker","status":"Unhealthy","description":"The worker heartbeat could not be read: connection refused","data":{"thresholdSeconds":120}}]}'
    $backlog = '{"status":"Degraded","checkedAt":"2026-11-22T15:00:00Z","checks":[{"name":"database","status":"Healthy","description":"The database answered.","data":{}},{"name":"queue","status":"Degraded","description":"The oldest queued run is 7200s old; the configured maximum is 900s.","data":{"queued":3,"running":1,"oldestAgeSeconds":7200,"thresholdSeconds":900,"queueEmpty":false}},{"name":"worker","status":"Healthy","description":"The worker is disabled and its heartbeat is current.","data":{"ageSeconds":12,"thresholdSeconds":120,"enabled":false}}]}'
    $staleWorker = '{"status":"Degraded","checkedAt":"2026-11-22T15:00:00Z","checks":[{"name":"database","status":"Healthy","description":"The database answered.","data":{}},{"name":"queue","status":"Healthy","description":"The queue is empty.","data":{"queued":0,"running":0,"oldestAgeSeconds":0,"thresholdSeconds":900,"queueEmpty":true}},{"name":"worker","status":"Degraded","description":"The worker heartbeat is 900s old; the configured maximum is 120s.","data":{"ageSeconds":900,"thresholdSeconds":120,"enabled":true}}]}'
    $unknown = '{"status":"Unknown","checkedAt":"2026-11-22T15:00:00Z","checks":[{"name":"database","status":"Healthy","description":"The database answered.","data":{}},{"name":"queue","status":"Unknown","description":"Operations:QueueMaxAgeSeconds is not configured; the queue age cannot be judged.","data":{}},{"name":"worker","status":"Unknown","description":"Operations:WorkerHeartbeatMaxAgeSeconds is not configured; the heartbeat age cannot be judged.","data":{}}]}'

    $cases = @(
        [pscustomobject]@{ Name = 'healthy'; Live = '200'; Ready = '200'; Edge = '404'; Ops = '200'; Json = $healthy; Listeners = '127.0.0.1:8081'; Expect = 'OK' },
        [pscustomobject]@{ Name = 'database down'; Live = '200'; Ready = '503'; Edge = '404'; Ops = '503'; Json = $dbDown; Listeners = '127.0.0.1:8081'; Expect = 'P1' },
        [pscustomobject]@{ Name = 'api not serving'; Live = '000'; Ready = '000'; Edge = '404'; Ops = '000'; Json = ''; Listeners = '127.0.0.1:8081'; Expect = 'P1' },
        [pscustomobject]@{ Name = 'edge exposes /health/ops'; Live = '200'; Ready = '200'; Edge = '200'; Ops = '200'; Json = $healthy; Listeners = '127.0.0.1:8081'; Expect = 'P1' },
        [pscustomobject]@{ Name = 'loopback bound publicly'; Live = '200'; Ready = '200'; Edge = '404'; Ops = '200'; Json = $healthy; Listeners = "0.0.0.0:8081`n127.0.0.1:8081"; Expect = 'P1' },
        [pscustomobject]@{ Name = 'queue backlog'; Live = '200'; Ready = '200'; Edge = '404'; Ops = '200'; Json = $backlog; Listeners = '127.0.0.1:8081'; Expect = 'P2' },
        [pscustomobject]@{ Name = 'stale worker'; Live = '200'; Ready = '200'; Edge = '404'; Ops = '200'; Json = $staleWorker; Listeners = '127.0.0.1:8081'; Expect = 'P2' },
        [pscustomobject]@{ Name = 'thresholds unset'; Live = '200'; Ready = '200'; Edge = '404'; Ops = '200'; Json = $unknown; Listeners = '127.0.0.1:8081'; Expect = 'P3' }
    )

    $failures = 0
    foreach ($case in $cases) {
        $verdict = Get-ProbeVerdict -LiveCode $case.Live -ReadyCode $case.Ready -EdgeOpsCode $case.Edge `
            -OpsCode $case.Ops -OpsJson $case.Json -Listeners $case.Listeners
        $expectedExit = if ($case.Expect -eq 'P1' -or $case.Expect -eq 'P2') { 1 } else { 0 }
        $ok = $verdict.Severity -eq $case.Expect -and $verdict.ExitCode -eq $expectedExit
        if (-not $ok) { $failures++ }
        $state = if ($ok) { 'pass' } else { 'FAIL' }
        Write-Host ("{0,-26} {1,-6} expected {2,-3} exit {3} -> {4}" -f $case.Name, $verdict.Severity, $case.Expect, $verdict.ExitCode, $state)
    }

    $summary = Format-Verdict -Verdict (Get-ProbeVerdict -LiveCode '200' -ReadyCode '503' -EdgeOpsCode '404' `
        -OpsCode '503' -OpsJson $dbDown -Listeners '127.0.0.1:8081') -LiveCode '200' -ReadyCode '503' `
        -EdgeOpsCode '404' -OpsCode '503' -Listeners '127.0.0.1:8081'
    if (-not $summary.Contains('P1') -or -not $summary.Contains('/health/ready')) {
        Write-Host 'FAIL: the rendered summary does not describe the database-down case'
        $failures++
    }

    if ($failures -gt 0) {
        Write-Host "self-test FAILED: $failures case(s)"
        return 1
    }

    Write-Host "self-test OK: $($cases.Count) cases"
    return 0
}

if ($SelfTest) {
    # Assign first: `exit (Invoke-SelfTest)` would swallow the function's own output into the exit argument.
    $selfTestExit = Invoke-SelfTest
    exit $selfTestExit
}

if ([string]::IsNullOrWhiteSpace($SshTarget)) {
    Write-Error '-SshTarget is required unless -SelfTest is used.'
    exit 2
}

$curlBinary = Resolve-Binary 'curl'
$sshBinary = Resolve-Binary 'ssh'

$liveCode = Invoke-Code "https://$PublicHost/health/live" $curlBinary
$readyCode = Invoke-Code "https://$PublicHost/health/ready" $curlBinary
$edgeOpsCode = Invoke-Code "https://$PublicHost/health/ops" $curlBinary

$opsCodeResult = Invoke-Remote "curl -s -o /dev/null -w '%{http_code}' --max-time 10 $LoopbackUrl" $sshBinary
$opsCode = if ($opsCodeResult.Exit -ne 0 -or [string]::IsNullOrWhiteSpace($opsCodeResult.Output)) { '000' } else { $opsCodeResult.Output.Trim() }

$opsBodyResult = Invoke-Remote "curl -s --max-time 10 $LoopbackUrl" $sshBinary
$opsJson = if ($opsBodyResult.Exit -eq 0) { $opsBodyResult.Output } else { '' }

# ss first; netstat is the fallback on an image without iproute2.
$listenersResult = Invoke-Remote "ss -ltn 2>/dev/null | awk 'NR>1 {print `$4}' | grep ':8081$' | sort -u || netstat -ltn 2>/dev/null | awk 'NR>2 {print `$4}' | grep ':8081$' | sort -u" $sshBinary
$listeners = $listenersResult.Output

$verdict = Get-ProbeVerdict -LiveCode $liveCode -ReadyCode $readyCode -EdgeOpsCode $edgeOpsCode `
    -OpsCode $opsCode -OpsJson $opsJson -Listeners $listeners

$markdown = Format-Verdict -Verdict $verdict -LiveCode $liveCode -ReadyCode $readyCode `
    -EdgeOpsCode $edgeOpsCode -OpsCode $opsCode -Listeners $listeners

Write-Output $markdown
Write-Output ''
Write-Output "SEVERITY=$($verdict.Severity)"

$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    [System.IO.File]::AppendAllText($SummaryPath, $markdown + [Environment]::NewLine, $utf8)
}

if (-not [string]::IsNullOrWhiteSpace($IssueBodyPath)) {
    $body = @(
        $markdown,
        '',
        '---',
        'This issue is managed by `.github/workflows/ops-probe.yml`: it comments here while the probe keeps failing and closes automatically when the probe passes. Runbook: [`docs/operations/runbook.md`](docs/operations/runbook.md).'
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($IssueBodyPath, $body, $utf8)
}

exit $verdict.ExitCode
