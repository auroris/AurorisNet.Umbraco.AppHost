#Requires -Version 7.0
# Installs a build into an existing site while keeping its configuration, case data and Umbraco content.
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Site,
    [string] $Build,
    [string] $Project,
    [string] $Assembly,
    [string[]] $MsBuildProperties = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
. (Join-Path $PSScriptRoot 'deployment/UpdateTransaction.ps1')

function Get-InstalledState([string] $SiteRoot) {
    $path = Get-SafePath $SiteRoot '.apphost/instance.json'
    if (![IO.File]::Exists($path)) { throw "Not a site created by new-site.ps1: $SiteRoot" }
    $state = Read-JsonFile $path
    if ($state.schemaVersion -notin @(2, 3)) { throw 'This site was created by different scripts and cannot be updated by this one.' }
    $app = Get-SafePath $SiteRoot app
    foreach ($entry in $state.appFiles.GetEnumerator()) {
        if ($entry.Key -eq 'appsettings.json') { continue }
        $file = Get-SafePath $app $entry.Key
        if (![IO.File]::Exists($file) -or (Get-AppFileHash $file) -ne $entry.Value) {
            throw "The installed application was changed: $($entry.Key). Make the change in the reference and publish it, or restore the file, then update again."
        }
    }
    return $state
}

function Assert-CompatibleBuild($State, $Incoming) {
    $installed = if ($State.Contains('assembly')) { $State.assembly } else { $script:ReferenceAssembly }
    if ($Incoming.assembly -ne $installed) { throw "This build runs $($Incoming.assembly) and the site runs $installed. Update it with a build of its own project." }
    if ([version]$Incoming.umbraco -lt [version]$State.umbraco) { throw 'This build runs an older Umbraco than the site. Restore a matching database backup instead of downgrading.' }
    if (([version]$Incoming.umbraco).Major -ne ([version]$State.umbraco).Major) { throw 'A major Umbraco upgrade needs its own migration plan before it is installed.' }
}

$siteRoot = (Resolve-Path -LiteralPath $Site).Path
$statePath = Get-SafePath $siteRoot '.apphost/instance.json'
$pending = Get-SafePath $siteRoot '.apphost/pending-update'
if (![IO.File]::Exists($statePath)) { throw "Not a site created by new-site.ps1: $siteRoot" }
if ($WhatIfPreference) {
    if ([IO.Directory]::Exists($pending)) { throw 'An update was interrupted. Run update-site.ps1 without -WhatIf to recover it.' }
    $state = Get-InstalledState $siteRoot
    if ($Build -or [IO.File]::Exists((Join-Path $PSScriptRoot build.json))) {
        $published = Resolve-Build $PSScriptRoot $Build $Project $Assembly $MsBuildProperties
        Assert-CompatibleBuild $state $published.Manifest
    }
}
if (!$PSCmdlet.ShouldProcess($siteRoot, 'Install the reference build and refresh the shared uSync definitions')) { return }

$lockPath = Get-SafePath $siteRoot '.apphost/run.lock'
try { $running = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { throw 'Stop the site before updating it.' }
try {
    Resolve-PendingUpdate $siteRoot
    $state = Get-InstalledState $siteRoot
    $app = Get-SafePath $siteRoot app
    foreach ($path in @($state.appFiles.Keys | Where-Object { $_ -match '\.(dll|exe)$' })) {
        $probe = [IO.File]::Open((Get-SafePath $app $path), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    }
    $published = Resolve-Build $PSScriptRoot $Build $Project $Assembly $MsBuildProperties
    $incoming = $published.Manifest
    Assert-CompatibleBuild $state $incoming
    $id = [guid]::NewGuid().ToString('N')
    $previous = if ($state.Contains('build')) { $state.build } else { $state.release }
    if ($previous -notmatch '^[\w.-]+$') { throw 'Invalid previous build name in the installed inventory.' }
    $backupName = "$previous-$id"
    $backup = Get-SafePath $siteRoot ('.apphost/backups/' + $backupName)
    $scratch = Get-SafePath $siteRoot '.apphost/preparing-update'
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    $stage = Get-SafePath $scratch app
    $sync = Get-SafePath $siteRoot uSync
    $stagedSync = Get-SafePath $scratch uSync
    try {
        Copy-Tree (Get-SafePath $published.Root app) $stage
        Assert-BuildFiles $stage $incoming.appFiles
        $settings = Get-SafePath $app 'appsettings.json'
        if ([IO.File]::Exists($settings)) { [IO.File]::Copy($settings, (Get-SafePath $stage 'appsettings.json'), $true) }
        foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -Force) {
            $relative = [IO.Path]::GetRelativePath($app, $file.FullName).Replace('\', '/')
            if ($relative -like 'umbraco/Logs/*' -or $relative -like 'umbraco/Data/TEMP/*') { continue }
            $source = Get-SafePath $app $relative
            if ($file.PSIsContainer -or $relative -eq 'appsettings.json' -or $state.appFiles.Contains($relative)) { continue }
            $target = Get-SafePath $stage $relative
            if (Test-Path -LiteralPath $target) { throw "A file created in this site conflicts with a new shared file: $relative" }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy($source, $target)
        }

        $hadSync = [IO.Directory]::Exists($sync)
        if ($hadSync) { Copy-Tree $sync $stagedSync }
        else { [IO.Directory]::CreateDirectory($stagedSync) | Out-Null }
        $changed = @(Copy-SyncDefinitions (Get-SafePath $stage uSync) $stagedSync)
        $removed = @(Remove-AppBlocksDefinitions $stagedSync)
        $state.schemaVersion = 3
        $state.build = $incoming.id
        $state.umbraco = $incoming.umbraco
        $state.assembly = $incoming.assembly
        $state.appFiles = $incoming.appFiles
        $state.sources = @(Get-BuildSources $incoming)
        $state.updateId = $id
        $state.Remove('release')
        Write-JsonFile (Get-SafePath $scratch 'instance.json') $state
        Write-JsonFile (Get-SafePath $scratch 'plan.json') @{ id = $id; backup = $backupName; hadSync = $hadSync }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
        # stop-point: update-staged
        [IO.Directory]::Move($scratch, $pending)
        # stop-point: journal-written
    } finally {
        if ([IO.Directory]::Exists($scratch)) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }

    # The stop-point comments mark where deployment/test-update-interruptions.ps1 stops a copy of this script.
    try {
        [IO.Directory]::Move($app, $backup)
        # stop-point: app-moved-to-backup
        [IO.Directory]::Move((Get-SafePath $pending app), $app)
        # stop-point: new-app-installed
        if ($hadSync) {
            [IO.Directory]::Move($sync, (Get-SafePath $pending 'previous-uSync'))
            # stop-point: previous-usync-moved
        }
        [IO.Directory]::Move((Get-SafePath $pending uSync), $sync)
        # stop-point: new-usync-installed
        [IO.File]::Move((Get-SafePath $pending 'instance.json'), $statePath, $true)
        # stop-point: inventory-replaced
    } finally {
        try { Resolve-PendingUpdate $siteRoot }
        catch { throw "Keep the site stopped and rerun update-site.ps1 to finish recovery. $($_.Exception.Message)" }
    }
    Write-Host "Installed build $($incoming.id). The previous application is in $backup"
    Write-UncommittedWarning $incoming
    if ($removed.Count) { Write-Host "Removed the following uSync definitions installed by AppBlocks:"; $removed | ForEach-Object { Write-Host "  $_" } }
    if (!$changed.Count) { Write-Host 'The shared uSync definitions are unchanged.' }
    else {
        Write-Host "Updated the following shared uSync definitions in $sync"
        $changed | ForEach-Object { Write-Host "  $_" }
        Write-Host 'The site imports them at its next start. If automatic import is disabled, use Settings > uSync to report and import.'
    }
} finally { $running.Dispose() }
