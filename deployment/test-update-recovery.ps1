#Requires -Version 7.0
# Exercise the actual install scripts with tiny disposable builds. No Umbraco installation is required.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot SiteTools.ps1)
. (Join-Path $PSScriptRoot UpdateTransaction.ps1)
$scripts = Split-Path -Parent $PSScriptRoot
$root = Get-SafePath ([IO.Path]::GetTempPath()) ('apphost-update-test-' + [guid]::NewGuid().ToString('N'))

function Assert-Equal($Expected, $Actual) {
    if ($Expected -cne $Actual) { throw "Expected '$Expected', got '$Actual'." }
}
function Assert-Rejected([scriptblock] $Action, [string] $Reason) {
    try { & $Action } catch { if ($_.Exception.Message -like "*$Reason*") { return }; throw }
    throw "Expected rejection: $Reason"
}
function New-TestBuild([string] $Id) {
    $build = Get-SafePath $root $Id
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

try {
    $old = New-TestBuild first
    $new = New-TestBuild second
    $site = Get-SafePath $root site
    $newSite = Join-Path $scripts new-site.ps1
    $update = Join-Path $scripts update-site.ps1
    $extra = Get-SafePath $new 'app/extra.dll'
    [IO.File]::WriteAllText($extra, 'unlisted')
    Assert-Rejected { & $newSite -Name Trial -Build $new -Destination $site } 'Unlisted build file'
    Assert-Equal $false (Test-Path -LiteralPath $site)
    [IO.File]::Delete($extra)
    & $newSite -Name Trial -Build $old -Destination $site
    $settings = Get-SafePath $new 'app/appsettings.json'
    $original = [IO.File]::ReadAllBytes($settings)
    [IO.File]::WriteAllText($settings, '{"changed":true}')
    Assert-Rejected { & $update -Site $site -Build $new -WhatIf } 'Changed build file'
    Assert-Rejected { & $update -Site $site -Build $new } 'Changed build file'
    [IO.File]::Delete($settings)
    Assert-Rejected { & $update -Site $site -Build $new } 'Missing build file'
    [IO.File]::WriteAllBytes($settings, $original)
    $inventory = Get-SafePath $site '.apphost/instance.json'
    $snapshot = Get-BinaryHash $inventory
    $liveApp = Get-SafePath $site 'app/Test.dll'
    $liveSync = Get-SafePath $site 'uSync/Dictionary/shared.config'
    $custom = Get-SafePath $site 'uSync/Dictionary/operator.config'
    [IO.File]::WriteAllText($custom, 'operator')
    if ($IsWindows) {
        # Force the last rename to fail after both live directories have been replaced.
        $handle = [IO.File]::Open($inventory, 'Open', 'Read', 'Read')
        try { Assert-Rejected { & $update -Site $site -Build $new } 'Move' } finally { $handle.Dispose() }
        Assert-Equal $snapshot (Get-BinaryHash $inventory)
        Assert-Equal first ([IO.File]::ReadAllText($liveApp))
        Assert-Equal first ([IO.File]::ReadAllText($liveSync))
        Assert-Equal operator ([IO.File]::ReadAllText($custom))
        Assert-Equal $false (Test-Path -LiteralPath (Get-SafePath $site '.apphost/pending-update'))
    }

    # Model a terminated process at each rename boundary. Recover twice to cover an operator retry.
    foreach ($hadSync in @($true, $false)) {
        foreach ($step in 0..5) {
            $trial = Get-SafePath $root "cut-$hadSync-$step"
            $pending = Get-SafePath $trial '.apphost/pending-update'
            $id = [guid]::NewGuid().ToString('N')
            $backup = Get-SafePath $trial ".apphost/backups/old-$id"
            $statePath = Get-SafePath $trial '.apphost/instance.json'
            $app = Get-SafePath $trial app
            $sync = Get-SafePath $trial uSync
            foreach ($dir in @($app, (Get-SafePath $pending app), (Get-SafePath $pending uSync), [IO.Path]::GetDirectoryName($backup))) {
                [IO.Directory]::CreateDirectory($dir) | Out-Null
            }
            [IO.File]::WriteAllText((Join-Path $app value), 'old')
            if ($hadSync) {
                [IO.Directory]::CreateDirectory($sync) | Out-Null
                [IO.File]::WriteAllText((Join-Path $sync value), 'old')
            }
            foreach ($file in @('app/value', 'uSync/value')) { [IO.File]::WriteAllText((Get-SafePath $pending $file), 'new') }
            Write-JsonFile $statePath @{ build = 'same' }
            Write-JsonFile (Get-SafePath $pending 'plan.json') @{ id = $id; backup = "old-$id"; hadSync = $hadSync }
            if ($step -ge 1) { [IO.Directory]::Move($app, $backup) }
            if ($step -ge 2) { [IO.Directory]::Move((Get-SafePath $pending app), $app) }
            if ($step -ge 3 -and $hadSync) { [IO.Directory]::Move($sync, (Get-SafePath $pending 'previous-uSync')) }
            if ($step -ge 4) { [IO.Directory]::Move((Get-SafePath $pending uSync), $sync) }
            if ($step -eq 5) { Write-JsonFile $statePath @{ build = 'same'; updateId = $id } }
            Resolve-PendingUpdate $trial
            Resolve-PendingUpdate $trial
            $expected = if ($step -eq 5) { 'new' } else { 'old' }
            Assert-Equal $expected ([IO.File]::ReadAllText((Join-Path $app value)))
            Assert-Equal ($hadSync -or $step -eq 5) ([IO.Directory]::Exists($sync))
            if ([IO.Directory]::Exists($sync)) { Assert-Equal $expected ([IO.File]::ReadAllText((Join-Path $sync value))) }
            Assert-Equal ($step -eq 5) ([IO.Directory]::Exists($backup))
        }
    }
    & $update -Site $site -Build $new
    Assert-Equal second (Read-JsonFile $inventory).build
    Assert-Equal second ([IO.File]::ReadAllText($liveApp))
    Assert-Equal second ([IO.File]::ReadAllText($liveSync))
    Assert-Equal operator ([IO.File]::ReadAllText($custom))
    Write-Host 'PASS: build verification, update rollback, interruption recovery and retry.'
} finally {
    if ([IO.Directory]::Exists($root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
