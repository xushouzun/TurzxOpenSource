#requires -Version 7.0
[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/verification-process')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { $ResultsDirectory } else { Join-Path $projectRoot $ResultsDirectory }
$runDirectory = (New-Item -ItemType Directory -Path (Join-Path $root ([guid]::NewGuid().ToString('N')))).FullName
$report = @{ commands = @() }
$results = @()

# Load only the actual helper, without running the full build/test/package workflow.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'verify-project.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-CheckedProcess' }, $true)
if ($null -eq $definition) { throw 'Invoke-CheckedProcess was not found.' }
. ([scriptblock]::Create($definition.Extent.Text))
$pwsh = (Get-Process -Id $PID).Path

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}

function Invoke-ExpectedFailure {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$Status, [int]$TimeoutSeconds = 10)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failed = $false
    try {
        Invoke-CheckedProcess -Name $Name -Executable $Executable -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -PipeTimeoutMilliseconds 250 -CleanupTimeoutMilliseconds 1000 | Out-Null
    } catch { $failed = $true }
    $watch.Stop()
    Assert-Condition $failed "$Name did not fail."
    $entry = $report.commands[-1]
    Assert-Condition ($entry.status -eq $Status) "$Name status was $($entry.status), expected $Status."
    Assert-Condition ([IO.File]::Exists($entry.stdout) -and [IO.File]::Exists($entry.stderr)) "$Name did not retain both log files."
    Assert-Condition ([IO.File]::Exists((Join-Path $runDirectory "$Name.process.json"))) "$Name did not retain process status."
    Assert-Condition ($watch.Elapsed.TotalSeconds -lt 15) "$Name exceeded the overall bounded-cleanup deadline."
    return @{ name = $Name; status = 'passed'; observedStatus = $entry.status; elapsedMs = $watch.Elapsed.TotalMilliseconds }
}

$childPidPath = Join-Path $runDirectory 'descendant.pid'
try {
    $successScript = Join-Path $runDirectory 'success.ps1'
    @'
[Console]::Out.Write('stdout-head' + ('x' * 1048576) + 'stdout-tail')
[Console]::Error.Write('stderr-head' + ('y' * 1048576) + 'stderr-tail')
'@ | Set-Content -LiteralPath $successScript -Encoding utf8
    $success = Invoke-CheckedProcess -Name 'success' -Executable $pwsh -Arguments @('-NoProfile', '-File', $successScript) -TimeoutSeconds 10
    Assert-Condition ($success.StdoutTruncated -and $success.StderrTruncated) 'Large returned text was not bounded.'
    Assert-Condition ($success.Stdout.Length -eq 65536 -and $success.Stderr.Length -eq 65536) 'Returned excerpts exceeded their limit.'
    Assert-Condition (([IO.File]::ReadAllText((Join-Path $runDirectory 'success.stdout.log'))).EndsWith('stdout-tail')) 'Full stdout tail was lost.'
    Assert-Condition (([IO.File]::ReadAllText((Join-Path $runDirectory 'success.stderr.log'))).EndsWith('stderr-tail')) 'Full stderr tail was lost.'
    Assert-Condition ($report.commands[-1].outputComplete) 'Successful output was not marked complete.'
    $results += @{ name = 'success-and-large-output'; status = 'passed'; stdoutBytes = $report.commands[-1].stdoutBytes; stderrBytes = $report.commands[-1].stderrBytes }

    $nonzero = Join-Path $runDirectory 'nonzero.ps1'
    "[Console]::Out.WriteLine('nonzero-stdout'); [Console]::Error.WriteLine('nonzero-stderr'); exit 7" | Set-Content -LiteralPath $nonzero -Encoding utf8
    $results += Invoke-ExpectedFailure 'nonzero' $pwsh @('-NoProfile', '-File', $nonzero) 'unexpectedExitCode'
    Assert-Condition ($report.commands[-1].exitCode -eq 7) 'Nonzero exit code was not retained.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $runDirectory 'nonzero.stderr.log') -Raw) -match 'nonzero-stderr') 'Nonzero stderr was lost.'

    $timeout = Join-Path $runDirectory 'timeout.ps1'
    "[Console]::Out.WriteLine('timeout-stdout'); [Console]::Out.Flush(); [Console]::Error.WriteLine('timeout-stderr'); [Console]::Error.Flush(); Start-Sleep -Seconds 60" | Set-Content -LiteralPath $timeout -Encoding utf8
    $results += Invoke-ExpectedFailure 'timeout' $pwsh @('-NoProfile', '-File', $timeout) 'timedOut' -TimeoutSeconds 3
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $runDirectory 'timeout.stdout.log') -Raw) -match 'timeout-stdout') 'Partial stdout before timeout was lost.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $runDirectory 'timeout.stderr.log') -Raw) -match 'timeout-stderr') 'Partial stderr before timeout was lost.'

    $results += Invoke-ExpectedFailure 'missing-executable' (Join-Path $runDirectory 'does-not-exist.exe') @() 'failed'

    if ($IsWindows) {
        $child = Join-Path $runDirectory 'descendant.ps1'
        "[IO.File]::WriteAllText('$($childPidPath.Replace("'", "''"))', [string]`$PID); Start-Sleep -Seconds 60" | Set-Content -LiteralPath $child -Encoding utf8
        $wrapper = Join-Path $runDirectory 'wrapper.cmd'
        @"
@echo off
start "" /b "$pwsh" -NoProfile -WindowStyle Hidden -File "$child"
:wait
if not exist "$childPidPath" goto wait
echo inherited-stdout
echo inherited-stderr 1>&2
exit /b 0
"@ | Set-Content -LiteralPath $wrapper -Encoding ascii
        $results += Invoke-ExpectedFailure 'inherited-writers' 'cmd.exe' @('/d', '/c', $wrapper) 'pipesTimedOut'
        Assert-Condition ((Get-Content -LiteralPath (Join-Path $runDirectory 'inherited-writers.stdout.log') -Raw) -match 'inherited-stdout') 'Inherited-pipe stdout was lost.'
        Assert-Condition ((Get-Content -LiteralPath (Join-Path $runDirectory 'inherited-writers.stderr.log') -Raw) -match 'inherited-stderr') 'Inherited-pipe stderr was lost.'
        $descendant = [Diagnostics.Process]::GetProcessById([int]([IO.File]::ReadAllText($childPidPath)))
        try { Assert-Condition (!$descendant.HasExited) 'The descendant must still own its inherited writers at return.' } finally { $descendant.Dispose() }
        Assert-Condition (!$report.commands[-1].outputComplete) 'Aborted inherited-pipe output was incorrectly marked complete.'
    }
    Write-Host "Passed $($results.Count) real-process regression cases. Evidence: $runDirectory"
}
finally {
    if ([IO.File]::Exists($childPidPath)) {
        try {
            $descendant = [Diagnostics.Process]::GetProcessById([int]([IO.File]::ReadAllText($childPidPath)))
            try { if (!$descendant.HasExited) { $descendant.Kill($true); $null = $descendant.WaitForExit(3000) } } finally { $descendant.Dispose() }
        } catch [ArgumentException] { }
    }
    @{ cases = $results; commands = $report.commands } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'regression.json') -Encoding utf8
}
