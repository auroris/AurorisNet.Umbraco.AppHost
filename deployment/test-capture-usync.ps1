#Requires -Version 7.0
param([string] $BuildOutput = (Join-Path $PSScriptRoot '../src/UmbracoAppHost.Web/bin/Debug/net10.0'))
. (Join-Path $PSScriptRoot SiteTools.ps1)
$capture = Join-Path $PSScriptRoot '../capture-usync.ps1'
$build = (Resolve-Path -LiteralPath $BuildOutput).Path
$root = [IO.Directory]::CreateTempSubdirectory('apphost-capture-').FullName
$project = Join-Path $root project
$instance = Join-Path $root instance
$working = Join-Path $instance uSync
$export = Join-Path $project export
function Put([string] $Folder, [string] $Relative, [string] $Value) {
    $file = Get-SafePath $Folder $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
    [IO.File]::WriteAllText($file, $Value)
}
function Assert-True($Value, [string] $Message) { if (!$Value) { throw $Message } }
try {
    Put $project 'Example.csproj' '<Project />'
    Put $working 'usync.config' '<uSync />'
    Put $export 'Dictionary/old.config' '<Dictionary Alias="Old" />'
    Put $working 'Content/site.config' '<Content Alias="Installer-owned page" />'
    Put $working 'ContentTypes/appactions.config' '<ContentType Alias="Changed AppBlocks definition" />'
    Put $working 'Templates/memo.config' '<Template Key="07fa397e-fd5b-44af-a054-a556f424ccf5" Alias="Memo" />'
    $choice = 'Dictionary/local.config'
    $shared = [xml][IO.File]::ReadAllText((Join-Path $build 'uSync/Dictionary/search.config'))
    $shared.DocumentElement.SetAttribute('Alias', 'Changed shared entry')
    Put $working 'Dictionary/renamed.config' $shared.OuterXml
    $shared.DocumentElement.SetAttribute('Key', [guid]::NewGuid().ToString())
    $shared.DocumentElement.SetAttribute('Alias', 'Local entry')
    Put $working $choice $shared.OuterXml
    Put $instance '.apphost/runtime.json' (@{ projectRoot=$project; hostExport=$export; uSyncRoot=(Join-Path $build uSync) } | ConvertTo-Json)
    $rows = @(& $capture -InstanceRoot $instance)
    foreach ($path in @('Dictionary/old.config','Templates/memo.config','Dictionary/renamed.config','ContentTypes/appactions.config')) {
        Assert-True (($rows | Where-Object Path -eq $path).Action -eq 'Review') "Expected a review item for $path."
    }
    Assert-True ('Content/site.config' -notin $rows.Path) 'Offered an unowned content file.'
    $blocked = $false
    try { & $capture -InstanceRoot $instance -Path @($choice, 'Dictionary/renamed.config') }
    catch { if ($_.Exception.Message -notlike '*requires review*') { throw }; $blocked = $true }
    Assert-True $blocked 'A renamed upstream definition was accepted.'
    Put $project 'Views/Memo.cshtml' '<main>Memo</main>'
    & $capture -InstanceRoot $instance -Path $choice -WhatIf
    Assert-True (!(Test-Path -LiteralPath (Join-Path $export $choice))) 'A preview or rejected selection copied a file.'
    $paths = @($choice, 'Templates/memo.config')
    & $capture -InstanceRoot $instance -Path $paths | Out-Host
    $remaining = @(& $capture -InstanceRoot $instance)
    Assert-True (@(& $capture -InstanceRoot $instance -Path $paths).Count -eq 0) 'Repeating the same selection copied it again.'
    foreach ($path in $paths) {
        Assert-True ((Get-BinaryHash (Join-Path $working $path)) -eq (Get-BinaryHash (Join-Path $export $path))) "Bytes changed for $path."
        Assert-True ($path -notin $remaining.Path) "Already captured file still offered: $path."
    }
    Write-Host 'PASS: preview, ownership by key, selection, missing views, installer content, deletions and repeat capture.'
} finally {
    if ([IO.Path]::GetDirectoryName($root) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        [IO.Path]::GetFileName($root) -like 'apphost-capture-*') { Remove-Item -LiteralPath $root -Recurse -Force }
}
