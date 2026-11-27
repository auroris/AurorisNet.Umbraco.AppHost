#Requires -Version 7.0
# Stops an update at each rename of its transaction, as a terminated process would, and checks that the next run
# recovers. Copies of the scripts replace one `# stop-point: <name>` comment with a process exit that skips every
# finally block. Also checks that reset.ps1 refuses while an update's journal exists. No Umbraco installation or
# dotnet is required: the builds are tiny disposable folders.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot SiteTools.ps1)
$scripts = Split-Path -Parent $PSScriptRoot
$root = Get-SafePath ([IO.Path]::GetTempPath()) ('apphost-interruption-test-' + [guid]::NewGuid().ToString('N'))
$pwsh = (Get-Process -Id $PID).Path
$stopped = 99

# Every stop point in the scripts, by file. A point that is missing, repeated or not listed here fails the test, so a
# change to the transaction cannot drop or add one unnoticed.
$stopPoints = [ordered]@{
    'update-site.ps1' = @('update-staged', 'journal-written', 'app-moved-to-backup', 'new-app-installed', 'previous-usync-moved', 'new-usync-installed', 'inventory-replaced')
    'deployment/UpdateTransaction.ps1' = @('restore-live-set-aside', 'restore-previous-returned', 'restore-usync-set-aside', 'journal-retired')
}

function Assert-True([bool] $Condition, [string] $Message) { if (!$Condition) { throw $Message } }

function New-TestBuild([string] $Id) {
    $build = Get-SafePath $root "build-$Id"
    $app = Get-SafePath $build app
    [IO.Directory]::CreateDirectory((Get-SafePath $app 'uSync/Dictionary')) | Out-Null
    Write-JsonFile (Get-SafePath $app 'appsettings.json') @{}
    foreach ($relative in @('Test.dll', 'uSync/usync.config', 'uSync/Dictionary/shared.config')) {
        [IO.File]::WriteAllText((Get-SafePath $app $relative), $Id)
    }
    $files = @{}
    foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse) {
        $files[[IO.Path]::GetRelativePath($app, $file.FullName).Replace('\', '/')] = Get-AppFileHash $file.FullName
    }
    Write-JsonFile (Get-SafePath $build build.json) @{ schemaVersion = 3; id = $Id; assembly = 'Test.dll'; umbraco = '17.0'; appFiles = $files }
    return $build
}

function Get-StopLine([string[]] $Lines, [string] $Point, [string] $File) {
    $found = @(for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match "^\s*# stop-point: $([regex]::Escape($Point))\s*$") { $i } })
    Assert-True ($found.Count -eq 1) "Expected one '# stop-point: $Point' in $File, found $($found.Count)."
    return $found[0]
}

# A copy of the scripts that exits at $Point, or runs unchanged when $Point is empty.
$copies = @{}
function Get-Scripts([string] $Point) {
    if ($copies.Contains($Point)) { return $copies[$Point] }
    $copy = Get-SafePath $root ('scripts-' + $(if ($Point) { $Point } else { 'unchanged' }))
    foreach ($file in @('update-site.ps1', 'new-site.ps1', 'reset.ps1')) {
        [IO.Directory]::CreateDirectory($copy) | Out-Null
        [IO.File]::Copy((Join-Path $scripts $file), (Get-SafePath $copy $file))
    }
    Copy-Tree $PSScriptRoot (Get-SafePath $copy deployment)
    # reset.ps1 resolves the saved export of the checkout it runs from; the instances below use their own.
    $export = Get-SafePath $copy 'Migrations/1.0.0-Current/uSync'
    [IO.Directory]::CreateDirectory($export) | Out-Null
    [IO.File]::WriteAllText((Get-SafePath $export usync.config), '<uSync />')
    if ($Point) {
        $file = @($stopPoints.Keys | Where-Object { $Point -in $stopPoints[$_] })[0]
        $path = Get-SafePath $copy $file
        $lines = [IO.File]::ReadAllLines($path)
        $index = Get-StopLine $lines $Point $file
        $lines[$index] = ($lines[$index] -replace '#.*$', '') + "[Environment]::Exit($stopped)"
        [IO.File]::WriteAllLines($path, $lines)
    }
    $copies[$Point] = $copy
    return $copy
}

function Invoke-Script([string] $Point, [string] $Script, [string[]] $Arguments) {
    $output = & $pwsh -NoProfile -NonInteractive -File (Get-SafePath (Get-Scripts $Point) $Script) @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output.Trim() }
}

function Invoke-Update([string] $Site, [string] $Point) {
    $result = Invoke-Script $Point update-site.ps1 @('-Site', $Site, '-Build', $script:second)
    if ($Point) { Assert-True ($result.Code -eq $stopped) "The update did not stop at ${Point} (exit $($result.Code)): $($result.Output)" }
    else { Assert-True ($result.Code -eq 0) "The update after the stop failed (exit $($result.Code)): $($result.Output)" }
}

function Read-Text([string] $Path) { if ([IO.File]::Exists($Path)) { [IO.File]::ReadAllText($Path) } else { '<missing>' } }

function Assert-Consistent([string] $Site, [string] $Case) {
    # Between runs: while no journal records the update, the application is the build the inventory names.
    if ([IO.Directory]::Exists((Get-SafePath $Site '.apphost/pending-update'))) { return }
    $build = (Read-JsonFile (Get-SafePath $Site '.apphost/instance.json')).build
    Assert-True ((Read-Text (Get-SafePath $Site 'app/Test.dll')) -eq $build) "${Case}: no journal, but the application is not build $build."
}

function Assert-Installed([string] $Site, [bool] $HadSync, [string] $Case) {
    foreach ($leftover in @('pending-update', 'preparing-update')) {
        Assert-True (![IO.Directory]::Exists((Get-SafePath $Site ".apphost/$leftover"))) "${Case}: .apphost/$leftover was left behind."
    }
    Assert-True ((Read-JsonFile (Get-SafePath $Site '.apphost/instance.json')).build -eq 'second') "${Case}: the inventory does not name the new build."
    Assert-True ((Read-Text (Get-SafePath $Site 'app/Test.dll')) -eq 'second') "${Case}: the application is not the new build."
    Assert-True ((Read-Text (Get-SafePath $Site 'uSync/Dictionary/shared.config')) -eq 'second') "${Case}: the uSync folder lacks the new build's definitions."
    if ($HadSync) { Assert-True ((Read-Text (Get-SafePath $Site 'uSync/Dictionary/operator.config')) -eq 'operator') "${Case}: the operator's uSync file was lost." }
}

function Get-Snapshot([string] $Site) {
    @(Get-ChildItem -LiteralPath $Site -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Site, $_.FullName)
        if ($_.PSIsContainer) { "$relative/" } else { "$relative $(Get-BinaryHash $_.FullName)" }
    }) -join "`n"
}

$sites = 0
function New-Site([bool] $HadSync) {
    $script:sites++
    $site = Get-SafePath $root "site-$($script:sites)"
    Copy-Tree $templates[$HadSync] $site
    return $site
}

try {
    # The stop points the scripts carry are exactly the ones above.
    foreach ($file in $stopPoints.Keys) {
        $lines = [IO.File]::ReadAllLines((Join-Path $scripts $file))
        foreach ($point in $stopPoints[$file]) { Get-StopLine $lines $point $file | Out-Null }
        $unknown = @($lines | Where-Object { $_ -match '^\s*# stop-point: (\S+)' -and $Matches[1] -notin $stopPoints[$file] })
        Assert-True ($unknown.Count -eq 0) "Stop points in $file that this test does not exercise: $($unknown -join '; ')"
    }

    $first = New-TestBuild first
    $script:second = New-TestBuild second
    $templates = @{}
    foreach ($hadSync in @($true, $false)) {
        $template = Get-SafePath $root "template-$hadSync"
        & (Get-SafePath (Get-Scripts '') new-site.ps1) -Name Trial -Build $first -Destination $template 6>$null | Out-Null
        if ($hadSync) { [IO.File]::WriteAllText((Get-SafePath $template 'uSync/Dictionary/operator.config'), 'operator') }
        else { Remove-Item -LiteralPath (Get-SafePath $template uSync) -Recurse -Force }
        # What a start of the published site writes; reset.ps1 reads it. The content root is app/.
        Write-JsonFile (Get-SafePath $template '.apphost/runtime.json') @{
            projectRoot = (Get-SafePath $template app); managedDatabase = $true; uSyncRoot = (Get-SafePath $template 'app/uSync'); hostExport = $null
        }
        $templates[$hadSync] = $template
    }

    # A stop after each rename of the update, then a complete run. The cleanup's journal-retired stop leaves the scratch
    # folder; update-staged leaves it before the journal exists.
    $firstStops = @(
        @($true, 'update-staged'), @($true, 'journal-written'), @($true, 'app-moved-to-backup'), @($true, 'new-app-installed'),
        @($true, 'previous-usync-moved'), @($true, 'new-usync-installed'), @($true, 'inventory-replaced'), @($true, 'journal-retired'),
        @($false, 'journal-written'), @($false, 'app-moved-to-backup'), @($false, 'new-app-installed'),
        @($false, 'new-usync-installed'), @($false, 'inventory-replaced'), @($false, 'journal-retired'))
    foreach ($case in $firstStops) {
        $name = "stop at $($case[1]), uSync folder $($case[0])"
        $site = New-Site $case[0]
        Invoke-Update $site $case[1]
        Assert-Consistent $site $name
        Invoke-Update $site ''
        Assert-Installed $site $case[0] $name
    }

    # A second stop during the recovery of the first. With a uSync folder the restore renames act on uSync first; without
    # one, the uSync folder is set aside on its own and the restore renames act on the application.
    $recoveryStops = @(
        @($true, 'new-usync-installed', 'restore-live-set-aside'), @($true, 'new-usync-installed', 'restore-previous-returned'),
        @($true, 'new-app-installed', 'journal-retired'),
        @($false, 'new-usync-installed', 'restore-usync-set-aside'), @($false, 'new-usync-installed', 'restore-live-set-aside'),
        @($false, 'new-usync-installed', 'restore-previous-returned'))
    foreach ($case in $recoveryStops) {
        $name = "stop at $($case[1]), then at $($case[2]) during recovery, uSync folder $($case[0])"
        $site = New-Site $case[0]
        Invoke-Update $site $case[1]
        Invoke-Update $site $case[2]
        Assert-Consistent $site $name
        Invoke-Update $site ''
        Assert-Installed $site $case[0] $name
    }

    # reset.ps1 deletes the backup an interrupted update's journal restores from, so it refuses and changes nothing.
    foreach ($point in @('journal-written', 'app-moved-to-backup', 'new-app-installed', 'previous-usync-moved', 'new-usync-installed', 'inventory-replaced')) {
        $name = "reset after a stop at $point"
        $site = New-Site $true
        Invoke-Update $site $point
        $before = Get-Snapshot $site
        $reset = Invoke-Script '' reset.ps1 @('-InstanceRoot', $site, '-Force')
        Assert-True ($reset.Code -ne 0 -and $reset.Output -like '*An application update was interrupted*') "${name}: the reset did not refuse (exit $($reset.Code)): $($reset.Output)"
        Assert-True ((Get-Snapshot $site) -eq $before) "${name}: the refused reset changed the site."
        Invoke-Update $site ''
        Assert-Installed $site $true $name
        $reset = Invoke-Script '' reset.ps1 @('-InstanceRoot', $site, '-Force')
        Assert-True ($reset.Code -eq 0) "${name}: the reset after recovery failed (exit $($reset.Code)): $($reset.Output)"
    }

    # A reset with no journal deletes the instance's data and keeps the application.
    $site = New-Site $true
    $reset = Invoke-Script '' reset.ps1 @('-InstanceRoot', $site, '-Force')
    Assert-True ($reset.Code -eq 0) "The reset of a site with no interrupted update failed (exit $($reset.Code)): $($reset.Output)"
    foreach ($deleted in @('data', 'media', 'uSync', 'instance.json', '.apphost/backups')) {
        Assert-True (!(Test-Path -LiteralPath (Get-SafePath $site $deleted))) "The reset left $deleted."
    }
    Assert-True ((Read-Text (Get-SafePath $site 'app/Test.dll')) -eq 'first') 'The reset changed the application.'

    Write-Host "PASS: $($firstStops.Count) update stops, $($recoveryStops.Count) recovery stops, and reset refusing an interrupted update."
} finally {
    if ([IO.Directory]::Exists($root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
