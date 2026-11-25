#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Static', 'Unit', 'E2E', 'Package')]
    [string]$Stage = 'All',
    [string]$ResultsDirectory = 'artifacts/verification',
    [switch]$Package,
    [switch]$Media
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultRoot = if ([IO.Path]::IsPathRooted($ResultsDirectory)) {
    [IO.Path]::GetFullPath($ResultsDirectory)
} else { [IO.Path]::GetFullPath((Join-Path $projectRoot $ResultsDirectory)) }
# Each invocation has its own evidence; old coverage or TRX cannot satisfy a failed run.
$runDirectory = Join-Path $resultRoot ("{0}-{1}-{2}" -f $Stage.ToLowerInvariant(), (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$report = [ordered]@{
    stage = $Stage
    startedUtc = [DateTime]::UtcNow.ToString('o')
    status = 'running'
    commands = @()
    coverageScope = 'Coverage is reported separately for each test suite and its loaded Core/Media/Windows/Studio modules; overlapping suite percentages must not be added. CLI, physical USB/display behaviour and GUI interaction are not measured by these reports.'
}

function Invoke-CheckedProcess {
    param(
        [string]$Name, [string]$Executable, [string[]]$Arguments,
        [int]$ExpectedExitCode = 0, [int]$TimeoutSeconds = 900,
        [int]$PipeTimeoutMilliseconds = 10000, [int]$CleanupTimeoutMilliseconds = 1000
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdoutPath = Join-Path $runDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $runDirectory "$Name.stderr.log"
    $entry = [ordered]@{
        name = $Name; executable = $Executable; arguments = $Arguments
        expectedExitCode = $ExpectedExitCode; status = 'starting'
        stdout = $stdoutPath; stderr = $stderrPath; outputComplete = $false
    }
    $report.commands += $entry
    $files = @()
    $copies = @()
    $started = $false
    $copyCancellation = [Threading.CancellationTokenSource]::new()
    # This open-instance .NET delegate reads Task.Exception without a PowerShell
    # scriptblock on a background thread. Faults after bounded cleanup are observed too.
    $observeFault = [Delegate]::CreateDelegate([Func[Threading.Tasks.Task, AggregateException]],
        [Threading.Tasks.Task].GetProperty('Exception').GetMethod)
    try {
        # Create files before launch and stream immediately, so failures keep partial output.
        foreach ($path in @($stdoutPath, $stderrPath)) {
            $files += [IO.FileStream]::new($path, [IO.FileMode]::Create, [IO.FileAccess]::Write,
                [IO.FileShare]::Read, 65536, [IO.FileOptions]::Asynchronous)
        }
        Write-Host "Running $Name..."
        if (!$process.Start()) { throw "Could not start $Executable." }
        $started = $true
        $entry.status = 'running'
        $copies = @(
            $process.StandardOutput.BaseStream.CopyToAsync($files[0], 65536, $copyCancellation.Token)
            $process.StandardError.BaseStream.CopyToAsync($files[1], 65536, $copyCancellation.Token)
        )
        foreach ($copy in $copies) {
            $null = $copy.ContinueWith[AggregateException]($observeFault, [Threading.CancellationToken]::None,
                ([Threading.Tasks.TaskContinuationOptions]::OnlyOnFaulted -bor [Threading.Tasks.TaskContinuationOptions]::ExecuteSynchronously),
                [Threading.Tasks.TaskScheduler]::Default)
        }
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $entry.status = 'timedOut'
            throw "$Name exceeded $TimeoutSeconds seconds."
        }
        $entry.exitCode = $process.ExitCode
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$copies, $PipeTimeoutMilliseconds)) {
            $entry.status = 'pipesTimedOut'
            throw "$Name output pipes did not close within $PipeTimeoutMilliseconds milliseconds."
        }
        if ($process.ExitCode -ne $ExpectedExitCode) {
            $entry.status = 'unexpectedExitCode'
            throw "$Name returned $($process.ExitCode), expected $ExpectedExitCode. See $runDirectory."
        }
        $entry.status = 'passed'
    }
    catch {
        if ($entry.status -in @('starting', 'running')) { $entry.status = 'failed' }
        $entry.error = $_.Exception.Message
        throw
    }
    finally {
        $cleanupErrors = @()
        if ($started) {
            try {
                if (!$process.HasExited) {
                    $process.Kill($true)
                    if (!$process.WaitForExit($CleanupTimeoutMilliseconds)) { $cleanupErrors += 'Process exit after kill exceeded cleanup deadline.' }
                }
                if ($process.HasExited) { $entry.exitCode = $process.ExitCode }
            } catch { $cleanupErrors += $_.Exception.Message }
            # Close local readers even if a surviving descendant still owns its writer.
            try { $copyCancellation.Cancel() } catch { $cleanupErrors += $_.Exception.Message }
            foreach ($reader in @($process.StandardOutput, $process.StandardError)) {
                try { $reader.Dispose() } catch { $cleanupErrors += $_.Exception.Message }
            }
        }
        if ($copies.Count -gt 0) {
            try {
                if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$copies, $CleanupTimeoutMilliseconds)) {
                    $cleanupErrors += 'Output copy exceeded cleanup deadline; late faults are observed by the .NET continuation.'
                }
            } catch {
                # Cancellation/read closure is expected after a timeout; retain the original failure.
                $entry.copyCleanup = $_.Exception.GetBaseException().Message
            }
            $entry.outputComplete = @($copies | Where-Object { !$_.IsCompletedSuccessfully }).Count -eq 0
        }
        foreach ($file in $files) {
            try { $file.Dispose() } catch { $cleanupErrors += $_.Exception.Message }
        }
        $process.Dispose()
        $copyCancellation.Dispose()
        $entry.cleanupErrors = $cleanupErrors
        foreach ($streamName in @('stdout', 'stderr')) {
            $path = $entry[$streamName]
            if ([IO.File]::Exists($path)) { $entry["${streamName}Bytes"] = [IO.FileInfo]::new($path).Length }
        }
        $entry | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory "$Name.process.json") -Encoding utf8
    }
    # Callers inspect only small CLI messages. Keep full logs on disk and bound returned text.
    $result = @{}
    foreach ($streamName in @('Stdout', 'Stderr')) {
        $reader = [IO.StreamReader]::new($entry[$streamName.ToLowerInvariant()], [Text.Encoding]::UTF8)
        try {
            $buffer = [char[]]::new(65536)
            $length = $reader.ReadBlock($buffer, 0, $buffer.Length)
            $result[$streamName] = [string]::new($buffer, 0, $length)
            $result["${streamName}Truncated"] = !$reader.EndOfStream
        } finally { $reader.Dispose() }
    }
    return [pscustomobject]$result
}
function Read-TestEvidence {
    param([string]$Directory)
    [xml]$trx = Get-Content -LiteralPath (Join-Path $Directory 'tests.trx') -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total) {
        throw 'The test run must contain tests and all tests must pass without skips.'
    }
    $tests = @{ total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed }
    $coverageFiles = @(Get-ChildItem -LiteralPath $Directory -Recurse -Filter '*.cobertura.xml')
    # VSTest can keep an attachment both under its GUID and under In/<machine>.
    $coverageHashes = @($coverageFiles | Get-FileHash -Algorithm SHA256 | Select-Object -ExpandProperty Hash -Unique)
    if ($coverageHashes.Count -ne 1) { throw "Expected one unique Cobertura report, found $($coverageHashes.Count)." }
    [xml]$coverage = Get-Content -LiteralPath $coverageFiles[0].FullName -Raw
    if ([int]$coverage.coverage.'lines-valid' -le 0) { throw 'Coverage report has no instrumented lines.' }
    $summary = @{
        report = $coverageFiles[0].FullName
        linesCovered = [int]$coverage.coverage.'lines-covered'
        linesValid = [int]$coverage.coverage.'lines-valid'
        lineRate = [double]::Parse($coverage.coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
        branchRate = [double]::Parse($coverage.coverage.'branch-rate', [Globalization.CultureInfo]::InvariantCulture)
        packages = @($coverage.coverage.packages.package | ForEach-Object { @{ name = $_.name; lineRate = $_.'line-rate'; branchRate = $_.'branch-rate' } })
    }
    return @{ tests = $tests; coverage = $summary }
}

try {
    Push-Location $projectRoot
    try {
        if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
            $report.commit = (& git rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Could not determine Git HEAD.' }
            $report.workingTreeDirty = [bool](& git status --porcelain)
        } else {
            $report.commit = $null
            $report.workingTreeDirty = $null
            $report.snapshot = 'No Git metadata; the outer delivery runner must bind this extracted snapshot to its candidate SHA.'
        }
        if ($Stage -in @('All', 'Static')) {
            Invoke-CheckedProcess 'build' 'dotnet' @('build', 'TURZX.WinUsbDisplay.slnx', '-c', 'Release', '-warnaserror') | Out-Null
            Invoke-CheckedProcess 'verification-process-regression' 'pwsh' @('-NoProfile', '-File', 'tools/test-verification-process.ps1', '-ResultsDirectory', (Join-Path $runDirectory 'process-regression')) | Out-Null
            if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
                Invoke-CheckedProcess 'diff-check' 'git' @('diff', '--check') | Out-Null
            }
        }
        if ($Stage -in @('All', 'Unit')) {
            $testResults = Join-Path $runDirectory 'tests'
            $testArguments = @('test', 'tests/TURZX.WinUsbDisplay.Tests/TURZX.WinUsbDisplay.Tests.csproj', '-c', 'Release', '--settings', 'tools/coverage.runsettings', '--collect', 'Code Coverage', '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $testResults)
            if ($Stage -eq 'All') { $testArguments += '--no-build' }
            Invoke-CheckedProcess 'unit' 'dotnet' $testArguments | Out-Null
            $evidence = Read-TestEvidence $testResults
            $report.tests = $evidence.tests
            $report.coverage = $evidence.coverage
            Write-Host ("Tests: {0}; covered lines: {1}/{2} ({3:P2})." -f $report.tests.total, $report.coverage.linesCovered, $report.coverage.linesValid, $report.coverage.lineRate)
            $windowsResults = Join-Path $runDirectory 'windows-tests'
            $windowsArguments = @('test', 'tests/TURZX.WinUsbDisplay.Windows.Tests/TURZX.WinUsbDisplay.Windows.Tests.csproj', '-c', 'Release', '--settings', 'tools/coverage.runsettings', '--collect', 'Code Coverage', '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $windowsResults)
            if ($Stage -eq 'All') { $windowsArguments += '--no-build' }
            Invoke-CheckedProcess 'windows-unit' 'dotnet' $windowsArguments | Out-Null
            $windowsEvidence = Read-TestEvidence $windowsResults
            $report.windowsTests = $windowsEvidence.tests
            $report.windowsCoverage = $windowsEvidence.coverage
            Write-Host ("Windows tests: {0}; covered lines: {1}/{2} ({3:P2})." -f $report.windowsTests.total, $report.windowsCoverage.linesCovered, $report.windowsCoverage.linesValid, $report.windowsCoverage.lineRate)
        }
        if ($Stage -in @('All', 'E2E')) {
            if ($Stage -eq 'E2E') {
                Invoke-CheckedProcess 'cli-build' 'dotnet' @('build', 'src/TURZX.WinUsbDisplay.Cli', '-c', 'Release', '-warnaserror') | Out-Null
            }
            $cli = Join-Path $projectRoot 'src/TURZX.WinUsbDisplay.Cli/bin/Release/net10.0/TURZX.WinUsbDisplay.Cli.dll'
            if (!(Test-Path -LiteralPath $cli)) { throw "Build the solution first; CLI missing: $cli" }
            $help = Invoke-CheckedProcess 'cli-help' 'dotnet' @($cli, '--help') -TimeoutSeconds 30
            if ($help.Stdout -notmatch 'TURZX WinUsbDisplay CLI' -or $help.Stdout -notmatch 'brightness') { throw 'CLI help output is incomplete.' }
            foreach ($case in @(
                @{ Name = 'unknown'; Args = @('unknown') },
                @{ Name = 'brightness-range'; Args = @('brightness', '0', '101') },
                @{ Name = 'image-blank-path'; Args = @('image', '-1', ' ') },
                @{ Name = 'video-blank-path'; Args = @('video', '-1', ' ', '20') },
                @{ Name = 'video-rate'; Args = @('video', '0', 'ignored', '0') },
                @{ Name = 'frames-blank-background'; Args = @('frames', '-1', '20', ' ', '20') },
                @{ Name = 'frames-rate'; Args = @('frames', '0', '0') }
            )) {
                $invalid = Invoke-CheckedProcess "cli-$($case.Name)" 'dotnet' (@($cli) + $case.Args) -ExpectedExitCode 1 -TimeoutSeconds 30
                if ($invalid.Stderr -notmatch 'Invalid command or arguments') { throw "Unexpected CLI error output for $($case.Name)." }
            }
            $device = Invoke-CheckedProcess 'cli-negative-device' 'dotnet' @($cli, 'info', '-1') -ExpectedExitCode 1 -TimeoutSeconds 30
            if ($device.Stderr -notmatch 'Select a device index printed by list') { throw 'Unexpected invalid-device output.' }
            $report.offlineE2E = 'CLI help and eight invalid-input paths passed as real processes; no hardware I/O was attempted.'
            $smokeArguments = @('run', '--project', 'tools/TURZX.WinUsbDisplay.Smoke', '-c', 'Release')
            if ($Media) { $smokeArguments += @('--', '--media') }
            Invoke-CheckedProcess 'browser-smoke' 'dotnet' $smokeArguments | Out-Null
            $report.browserE2E = 'Real Chromium local HTML integration smoke passed; physical display and Studio GUI remain outside this automated check.'
            if ($Media) { $report.mediaE2E = 'Real FFmpeg PNG/H.264/MP4 conversion and frame-rate detection smoke passed.' }
        }
        if ($Stage -eq 'Package' -or ($Stage -eq 'All' -and $Package)) {
            Invoke-CheckedProcess 'package' 'pwsh' @('-NoProfile', '-File', 'tools/package-release.ps1') | Out-Null
            $archiveChecks = @()
            foreach ($archiveName in @('TURZX-Studio-win-x64.zip', 'TURZX-WinUsbDisplay-SDK.zip')) {
                $archivePath = Join-Path $projectRoot "artifacts/$archiveName"
                $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
                try {
                    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
                    $required = @('LICENSE')
                    if ($archiveName -like '*Studio*') {
                        $required += @('README.md', 'README.zh-CN.md')
                    }
                    foreach ($requiredFile in $required) {
                        if ($requiredFile -notin $entries) { throw "$archiveName is missing $requiredFile." }
                    }
                    $specific = if ($archiveName -like '*Studio*') {
                        @('TURZX.WinUsbDisplay.Studio.exe', 'coreclr.dll', 'licenses/Microsoft.NETCore.App/LICENSE.TXT', 'licenses/Microsoft.NETCore.App/THIRD-PARTY-NOTICES.TXT', 'licenses/Microsoft.WindowsDesktop.App/LICENSE', 'Driver/TURZX_display_driver.inf', 'Driver/TURZX_display_driver.cat', 'Driver/TURZX_display_driver.dll')
                    } else {
                        @('lib/TURZX.WinUsbDisplay.dll', 'lib/TURZX.WinUsbDisplay.xml', 'lib/TURZX.WinUsbDisplay.Media.dll', 'lib/TURZX.WinUsbDisplay.Media.xml', 'examples/SdkDiscover/SdkDiscover.csproj')
                    }
                    foreach ($required in $specific) {
                        if ($required -notin $entries) { throw "$archiveName is missing $required." }
                    }
                    $archiveChecks += @{ name = $archiveName; sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash; entries = $entries.Count }
                }
                finally { $archive.Dispose() }
            }
            $report.archives = $archiveChecks
            $extractedSdk = Join-Path $runDirectory 'extracted-sdk'
            [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $projectRoot 'artifacts/TURZX-WinUsbDisplay-SDK.zip'), $extractedSdk)
            Invoke-CheckedProcess 'packaged-sdk-discover' 'dotnet' @('run', '--project', (Join-Path $extractedSdk 'examples/SdkDiscover/SdkDiscover.csproj'), '-c', 'Release') | Out-Null
            $report.packagedSdk = 'Discovery example from the extracted SDK archive ran successfully; enumeration only, no USB writes.'
        }
        $report.status = 'passed'
    }
    finally { Pop-Location }
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    throw
}
finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding utf8
    Write-Host "Verification evidence: $runDirectory"
}
