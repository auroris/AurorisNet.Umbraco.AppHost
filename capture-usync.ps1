#Requires -Version 7.0
# Preview changes by default. Passing exact paths copies the selected definitions to the project's saved export.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstanceRoot = (Join-Path $PSScriptRoot reference),
    [string[]] $Path = @()
)
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
. (Join-Path $PSScriptRoot 'deployment/Read-ExportOwners.ps1')
$working = Get-SafePath $InstanceRoot 'uSync'
$runtimePath = Get-SafePath $InstanceRoot '.apphost/runtime.json'
if (!(Test-Path -LiteralPath $runtimePath) -or !(Test-Path -LiteralPath "$working/usync.config")) {
    throw 'Start the development site once and let uSync export the backoffice change before running capture.'
}
$runtime = Read-JsonFile $runtimePath
$project = $runtime.projectRoot
$export = $runtime.hostExport
if (!$export) { throw 'This site has no saved project uSync export. Edit installer-owned items in their installer. For a uSync-owned project, create its configured HostExport folder and restart the development site.' }
$projectFiles = @(Get-ChildItem -LiteralPath $project -File -Filter '*.csproj')
if ($projectFiles.Count -ne 1 -or [IO.Path]::GetFullPath($export) -eq [IO.Path]::GetFullPath($runtime.uSyncRoot)) {
    throw 'Use an instance started from a development checkout. A deployed site contains a merged copy of the exports.'
}
$build = Split-Path -Parent $runtime.uSyncRoot
$apps = @(Get-ChildItem -LiteralPath $build -File -Filter '*.runtimeconfig.json')
if ($apps.Count -ne 1) { throw 'Could not identify the host application in the recorded build output. Build and start the development site again.' }
$hostAssembly = $apps[0].Name -replace '\.runtimeconfig\.json$',''
$owners = Get-ExportOwners $build $hostAssembly
$saved = Get-SyncExportFiles $export $working
$current = Get-SyncExportFiles $working -IncludeOwned
$savedKeys = @{}
foreach ($relative in $saved.Keys) {
    $root = ([xml][IO.File]::ReadAllText($saved[$relative])).DocumentElement
    $key = $root.GetAttribute('Key')
    if ($key) { $savedKeys[$key] = $relative }
}
$changes = @{}
$workingKeys = @{}
foreach ($relative in ($current.Keys | Sort-Object)) {
    $xml = ([xml][IO.File]::ReadAllText($current[$relative])).DocumentElement
    $key = $xml.GetAttribute('Key')
    if ($key) { $workingKeys[$key] = @($workingKeys[$key] | Where-Object { $_ }) + $relative }
    $folder = $relative.Split('/')[0]
    if ($saved.ContainsKey($relative) -and (Get-TextHash $saved[$relative]) -eq (Get-TextHash $current[$relative])) { continue }
    $owner = $owners[$relative]
    if (!$owner -and $key) { $owner = $owners[$key] }
    $note = $null
    $action = 'Copy'
    if ($relative -in $script:RewrittenDefinitions) { continue }
    if ($owner) {
        if ($owner.Path -eq $relative -and $owner.Xml -ceq $xml.OuterXml) { continue }
        $action = 'Review'; $note = "Shipped by $($owner.Assembly)."
    } elseif ($relative -in $script:AppBlocksDefinitions) {
        $action = 'Review'; $note = 'Owned by an AppBlocks schema.'
    } elseif ($xml.LocalName -eq 'Empty' -or ($key -and $savedKeys.ContainsKey($key) -and $savedKeys[$key] -ne $relative)) {
        $action = 'Review'; $note = 'Rename or deletion. Review the saved file and its uSync marker together.'
    } elseif ($saved.ContainsKey($relative) -and $key -ne (([xml][IO.File]::ReadAllText($saved[$relative])).DocumentElement.GetAttribute('Key'))) {
        $action = 'Review'; $note = 'The saved and working items have different uSync keys at this path.'
    } elseif (!$saved.ContainsKey($relative) -and $folder -notin $script:SharedDefinitionFolders) {
        # New content, media, and installer-seeded records have no evidence of project ownership.
        continue
    } elseif ($folder -eq 'Templates') {
        $alias = $xml.GetAttribute('Alias')
        $view = Get-SafePath (Get-SafePath $project 'Views') "$alias.cshtml"
        if (!$alias -or !(Test-Path -LiteralPath $view -PathType Leaf)) {
            $action = 'Review'; $note = "Missing project view Views/$alias.cshtml."
        } elseif ((Get-Command git -ErrorAction SilentlyContinue) -and (git -C $project check-ignore -- "Views/$alias.cshtml" 2>$null)) {
            $action = 'Review'; $note = "Git ignores Views/$alias.cshtml. Include the view in the project and source control."
        } else { $note = "Include Views/$alias.cshtml in the same change." }
    }
    $change = if ($saved.ContainsKey($relative)) { 'Modified' } elseif ($owner) { 'External' } else { 'Added' }
    $changes[$relative] = [pscustomobject]@{ Action = $action; Change = $change; Path = $relative; Note = $note }
}
foreach ($paths in $workingKeys.Values) {
    if ($paths.Count -lt 2) { continue }
    foreach ($relative in $paths) {
        if ($changes.ContainsKey($relative) -and $changes[$relative].Action -eq 'Copy') {
            $changes[$relative].Action = 'Review'
            $changes[$relative].Note = "Multiple working files have the same uSync key: $($paths -join ', ')."
        }
    }
}
foreach ($relative in $saved.Keys) {
    if (!$current.ContainsKey($relative)) {
        $changes[$relative] = [pscustomobject]@{ Action = 'Review'; Change = 'Missing'; Path = $relative; Note = 'Absent from the working export. Confirm the deletion in source control.' }
    }
}
if (!$Path.Count) {
    $changes.Values | Sort-Object Path
    if (!$changes.Count) { Write-Host 'No project export changes.' }
    return
}
# Validate the whole selection before copying. Get-SafePath also rejects linked folders and traversal.
$selected = foreach ($item in ($Path | Select-Object -Unique)) {
    $relative = $item.Replace('\','/')
    if (!$changes.ContainsKey($relative)) {
        if ($saved.ContainsKey($relative) -and $current.ContainsKey($relative) -and
            (Get-TextHash $saved[$relative]) -eq (Get-TextHash $current[$relative])) { continue }
        throw "No eligible change for '$relative'. Run without -Path to see the available files."
    }
    if ($changes[$relative].Action -ne 'Copy') { throw "$relative requires review. $($changes[$relative].Note)" }
    [pscustomobject]@{ Source = Get-SafePath $working $relative; Target = Get-SafePath $export $relative; Change = $changes[$relative] }
}
foreach ($file in $selected) {
    if ($PSCmdlet.ShouldProcess($file.Target, 'Copy selected uSync export')) {
        $directory = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Target))
        $temporary = Join-Path $directory.FullName ([IO.Path]::GetRandomFileName())
        try {
            [IO.File]::Copy($file.Source, $temporary)
            [IO.File]::Move($temporary, $file.Target, $true)
        } finally { [IO.File]::Delete($temporary) }
        $file.Change
    }
}
