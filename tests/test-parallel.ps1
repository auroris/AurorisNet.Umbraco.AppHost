# Runs the tests in several processes at once. Umbraco keeps state in static fields, so a process runs one site at a
# time; the test classes are divided over the processes instead. test-parallel.timings.json holds the seconds each
# class took when -RecordTimings last ran, which is what keeps the processes about equally long.
[CmdletBinding()]
param(
    [ValidateRange(1, 16)] [int] $Shards = 3,
    [string] $Configuration = 'Debug',
    [string] $Results = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/test-parallel'),
    # Traits of tests to leave out, such as Category=OwnInstall.
    [string[]] $ExcludeTrait = @(),
    [switch] $NoBuild,
    [switch] $RecordTimings
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj'
$timingsFile = Join-Path $PSScriptRoot 'test-parallel.timings.json'
$testName = '^\s+((?:\w+\.)+\w+)\.\w+\s*(\(|$)'

if (!$NoBuild) {
    dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE) { throw 'The test project did not build.' }
}
$exe = @(Get-Item (Join-Path $PSScriptRoot "UmbracoAppHost.Tests/bin/$Configuration/*/UmbracoAppHost.Tests.exe"))
if ($exe.Count -ne 1) { throw "Expected one built UmbracoAppHost.Tests.exe for $Configuration, found $($exe.Count)." }
$exe = $exe[0].FullName

# A class nobody lists here would never run, so the classes come from the test executable itself.
$exclude = @($ExcludeTrait | Where-Object { $_ } | ForEach-Object { '--filter-not-trait'; $_ })
$listed = & $exe --list-tests --no-ansi @exclude
if ($LASTEXITCODE) { throw "Listing the tests failed with exit code $LASTEXITCODE." }
$tests = @($listed | ForEach-Object { if ($_ -match $testName) { $Matches[1] } })
$found = if (($listed -join "`n") -match 'found (\d+) test') { [int]$Matches[1] } else { -1 }
if ($tests.Count -eq 0 -or $tests.Count -ne $found) { throw "The test list names $found tests, of which $($tests.Count) could be read." }
$classes = @($tests | Sort-Object -Unique)

$timings = if (Test-Path -LiteralPath $timingsFile) { Get-Content -LiteralPath $timingsFile -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$known = @($classes | Where-Object { $timings.ContainsKey($_) } | ForEach-Object { [double]$timings[$_] } | Sort-Object)
# A class without a timing counts as the median one: most classes start no site and take a second or two.
$median = if ($known.Count) { $known[[int][Math]::Floor($known.Count / 2)] } else { 1.0 }
function Get-Seconds([string] $Class) { if ($timings.ContainsKey($Class)) { [double]$timings[$Class] } else { $median } }

# The longest classes first, each to the process with the least so far.
$Shards = [Math]::Min($Shards, $classes.Count)
$plan = @(1..$Shards | ForEach-Object { @{ Number = $_; Classes = [Collections.Generic.List[string]]::new(); Seconds = 0.0 } })
foreach ($class in $classes | Sort-Object @{ Expression = { Get-Seconds $_ }; Descending = $true }, @{ Expression = { $_ } }) {
    $shard = $plan | Sort-Object { $_.Seconds }, { $_.Number } | Select-Object -First 1
    $shard.Classes.Add($class)
    $shard.Seconds += Get-Seconds $class
}

$Results = [IO.Path]::GetFullPath($Results)
if (Test-Path -LiteralPath $Results) { Remove-Item -LiteralPath $Results -Recurse -Force }
$clock = [Diagnostics.Stopwatch]::StartNew()
$failed = $false
$ran = 0
try {
    foreach ($shard in $plan) {
        $shard.Folder = Join-Path $Results "shard-$($shard.Number)"
        [IO.Directory]::CreateDirectory($shard.Folder) | Out-Null
        $info = [Diagnostics.ProcessStartInfo]::new($exe)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($class in $shard.Classes) { $info.ArgumentList.Add('--filter-class'); $info.ArgumentList.Add($class) }
        foreach ($argument in $exclude + @('--report-xunit-trx', '--results-directory', $shard.Folder, '--no-progress', '--no-ansi')) { $info.ArgumentList.Add($argument) }
        $shard.Process = [Diagnostics.Process]::Start($info)
        $shard.Output = $shard.Process.StandardOutput.ReadToEndAsync()
        $shard.Error = $shard.Process.StandardError.ReadToEndAsync()
    }
    Write-Host "Running $($tests.Count) tests of $($classes.Count) classes in $Shards processes."

    $waiting = [Collections.Generic.List[object]]::new($plan)
    while ($waiting.Count) {
        Start-Sleep -Milliseconds 500
        foreach ($shard in @($waiting | Where-Object { $_.Process.HasExited })) {
            $waiting.Remove($shard) | Out-Null
            $shard.Process.WaitForExit()
            $output = $shard.Output.GetAwaiter().GetResult() + $shard.Error.GetAwaiter().GetResult()
            Set-Content -LiteralPath (Join-Path $shard.Folder 'output.log') -Value $output
            $total = if ($output -match '(?m)^\s+total: (\d+)') { [int]$Matches[1] } else { 0 }
            $failures = if ($output -match '(?m)^\s+failed: (\d+)') { [int]$Matches[1] } else { 0 }
            $ran += $total
            $passed = $shard.Process.ExitCode -eq 0
            if (!$passed) { $failed = $true }
            Write-Host ('Process {0}: {1} tests of {2} classes, {3} failed, after {4:mm\:ss}{5}' -f $shard.Number, $total, $shard.Classes.Count, $failures,
                $clock.Elapsed, $(if ($passed) { '' } else { " (exit code $($shard.Process.ExitCode))" }))
            # A process that passed has nothing more to say than its line above.
            if (!$passed) { Write-Host $output }
        }
    }
}
finally {
    # An interrupted run would otherwise leave sites running on their temporary folders.
    foreach ($shard in $plan) { if ($shard.Process -and !$shard.Process.HasExited) { $shard.Process.Kill($true) } }
}

if ($ran -ne $tests.Count) {
    $failed = $true
    Write-Host "$($tests.Count) tests were listed and $ran ran."
}
if ($RecordTimings -and !$failed) {
    $seconds = @{}
    foreach ($report in Get-ChildItem -LiteralPath $Results -Recurse -Filter *.trx) {
        foreach ($result in ([xml](Get-Content -LiteralPath $report.FullName -Raw)).TestRun.Results.UnitTestResult) {
            if (" $($result.testName)" -match $testName) { $seconds[$Matches[1]] += [timespan]::Parse($result.duration).TotalSeconds }
        }
    }
    $rounded = [ordered]@{}
    foreach ($class in $seconds.Keys | Sort-Object) { $rounded[$class] = [Math]::Max(1, [int][Math]::Round($seconds[$class])) }
    Set-Content -LiteralPath $timingsFile -Value ($rounded | ConvertTo-Json)
    Write-Host "Recorded the timings of $($rounded.Count) classes in $timingsFile."
}
Write-Host ('{0} after {1:mm\:ss}. The reports and each process''s output are in {2}.' -f $(if ($failed) { 'Failed' } else { 'Passed' }), $clock.Elapsed, $Results)
if ($failed) { exit 1 }
