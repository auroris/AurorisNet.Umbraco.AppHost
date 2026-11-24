#Requires -Version 7.0
# Integration checks for the site scripts: creates two disposable sites from one build, boots both, then
# updates one, verifies what an update preserves and what it refreshes, and starts the updated site. Run from the
# checkout, it also publishes a second build that changes a shared definition, as a release does, updates the site
# with it and checks that the site imports the definition at its start. Pass -Build to test a build made earlier.
[CmdletBinding()]
param(
    [string] $Build,
    [string] $OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('apphost-site-test-' + [guid]::NewGuid().ToString('N'))),
    [string[]] $MsBuildProperties = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot SiteTools.ps1)
$scripts = Split-Path -Parent $PSScriptRoot
$published = Resolve-Build $scripts $Build '' '' $MsBuildProperties
function Assert-True($Condition, [string] $Message) { if (!$Condition) { throw $Message } }

function Start-TestSite([string] $Site, [string] $Assembly) {
    $port = Get-Random -Minimum 47000 -Maximum 49000
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = Join-Path $Site app
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($arg in @($Assembly, '--InstanceRoot', $Site, '--urls', "http://127.0.0.1:$port")) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    return @{
        Site = $Site; Process = $process; Base = "http://127.0.0.1:$port"
        Stdout = $process.StandardOutput.ReadToEndAsync(); Stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Wait-TestSite($Running, [string] $Expected) {
    # Returns the home page once it answers 200 with the expected text, or the last answer when it never does. A first
    # start installs Umbraco and imports the whole export, which a busy machine takes minutes over.
    $deadline = [DateTime]::UtcNow.AddSeconds(300)
    $response = $null
    do {
        if ($Running.Process.HasExited) { throw "Published host exited: $($Running.Stderr.GetAwaiter().GetResult())" }
        try { $response = Invoke-WebRequest "$($Running.Base)/" -SkipHttpErrorCheck -TimeoutSec 5 } catch { }
        if ($response -and $response.StatusCode -eq 200 -and $response.Content -match $Expected) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $response
}

function Stop-TestSite($Running, [string] $Phase) {
    $process = $Running.Process
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $Running.Stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $Running.Site "$Phase.stdout.log")
    $Running.Stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $Running.Site "$Phase.stderr.log")
    $process.Dispose()
    # The instance lock may be held a moment after the process is gone, and an update needs it.
    $lock = Join-Path $Running.Site '.apphost/run.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Test-Path -LiteralPath $lock) {
        try { [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None).Dispose(); break } catch [IO.IOException] { }
        if ([DateTime]::UtcNow -gt $deadline) { throw "The stopped host still holds $lock." }
        Start-Sleep -Milliseconds 200
    }
}

# A build records the commit of every checkout it was made from, and each site records them with its build.
$recordsSources = [bool](Find-Git) -and !$Build
if ($recordsSources) {
    $names = @(Get-BuildSources $published.Manifest | ForEach-Object { $_.name })
    Assert-True ($names.Count -gt 1 -and $names[0] -eq (Split-Path -Leaf $scripts)) "The build records these source checkouts, not this one first and the ones it builds beside it: $($names -join ', ')."
    Assert-True (@(Get-BuildSources $published.Manifest | Where-Object { $_.commit -notmatch '^[0-9a-f]{40}$' }).Count -eq 0) 'A source checkout is recorded without its commit.'
}
New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$sites = @{}
foreach ($name in @('first', 'second')) {
    $site = Get-SafePath $OutputRoot $name
    & (Join-Path $scripts new-site.ps1) -Build $published.Root -Name ('Test' + $name) -Destination $site
    $sites[$name] = $site
    Assert-True (Test-Path (Join-Path $site 'uSync/usync.config')) 'The saved uSync export was not copied into the site.'
    Assert-True (Test-Path (Join-Path $site 'uSync/Content')) 'The starter content was not copied into the site.'
    $assembly = (Read-JsonFile (Join-Path $site '.apphost/instance.json')).assembly
    Assert-True ($assembly -eq 'UmbracoAppHost.Web.dll') "The site records $assembly as its application, not the reference launcher."
    $settings = Read-JsonFile (Join-Path $site instance.json)
    # Without a sign-in provider an anonymous request to a signed-in page sees the member sign-in form instead of a challenge.
    $settings.IdentityServer = @{ Enabled = $false }
    $settings.Accounts.SeedCurrentWindowsUserAsAdministrator = $false
    $settings.Umbraco.CMS.Global.UseHttps = $false
    Write-JsonFile (Join-Path $site instance.json) $settings
    if ($recordsSources) {
        $recorded = @(Get-BuildSources (Read-JsonFile (Join-Path $site '.apphost/instance.json')) | ForEach-Object { $_.commit })
        Assert-True (($recorded -join ' ') -eq (@(Get-BuildSources $published.Manifest | ForEach-Object { $_.commit }) -join ' ')) 'The site does not record the commits of its build.'
    }
    $running = Start-TestSite $site $assembly
    try {
        # The saved export's first root is the Sample Application, whose sign-in page names it.
        $expectedTitle = 'Sample Application'
        $response = Wait-TestSite $running $expectedTitle
        Assert-True ($response -and $response.StatusCode -eq 200) "$name home did not render. Logs: $site/app/umbraco/Logs"
        Assert-True ($response.Content -match $expectedTitle) "$name imported content missing."
        # The Sample Application's home is public; its Orders form uses the Signed-in page template.
        $signIn = Invoke-WebRequest "$($running.Base)/orders-form/" -SkipHttpErrorCheck
        Assert-True ($signIn.StatusCode -eq 200 -and $signIn.Content -match 'loginModel.Username') "$name did not offer the member sign-in form on /orders-form/."
        foreach ($path in @('/dist/scripts/site.js', '/dist/css/app.css', '/api/reports/ui/ir.js')) {
            $asset = Invoke-WebRequest "$($running.Base)$path" -SkipHttpErrorCheck
            Assert-True ($asset.StatusCode -eq 200 -and $asset.RawContentLength -gt 100) "Published asset missing: $path"
        }
        Set-Content (Join-Path $site media/preserved.txt) 'instance media'
        $media = Invoke-WebRequest "$($running.Base)/media/preserved.txt" -SkipHttpErrorCheck
        Assert-True ($media.StatusCode -eq 200) 'External media folder is not served.'
        Assert-True (Test-Path (Join-Path $site data/Umbraco.sqlite.db)) 'Database was not created outside app/.'
        # The first boot's import publishes the Orders data source, whose validation opens the sample database; a host
        # without the samples creates none, which test-downstream.ps1 checks.
        Assert-True (Test-Path (Join-Path $site data/ReportsSample.sqlite.db)) 'The import did not validate the Orders data source against its sample database.'
        $locked = $false
        try { $probe = [IO.File]::Open((Join-Path $site '.apphost/run.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
        catch [IO.IOException] { $locked = $true }
        Assert-True $locked 'The host did not hold its instance lock.'
        Write-Host "PASS: $name boots and serves its shell, both frontend bundles, reports and instance media."
    } finally { Stop-TestSite $running 'first-start' }
}
$firstState = Read-JsonFile (Join-Path $sites.first '.apphost/instance.json')
$secondState = Read-JsonFile (Join-Path $sites.second '.apphost/instance.json')
Assert-True (($firstState.appFiles | ConvertTo-Json -Compress) -eq ($secondState.appFiles | ConvertTo-Json -Compress)) 'Sites did not install identical builds.'
$firstSettings = Read-JsonFile (Join-Path $sites.first 'instance.json')
$secondSettings = Read-JsonFile (Join-Path $sites.second 'instance.json')
Assert-True ($firstSettings.Umbraco.CMS.Global.Id -ne $secondSettings.Umbraco.CMS.Global.Id) 'Sites reused an identity.'
$site = $sites.first
$preserved = @{}
foreach ($path in @('instance.json', 'data/Umbraco.sqlite.db', 'media/preserved.txt')) { $preserved[$path] = Get-AppFileHash (Join-Path $site $path) }
$appSettingsPath = Join-Path $site app/appsettings.json
$appSettings = Read-JsonFile $appSettingsPath
if (!$appSettings.Contains('UmbracoAppHost')) { $appSettings.UmbracoAppHost = @{} }
$appSettings.UmbracoAppHost.AdminPassword = 'Release-test-admin-password91!'
Write-JsonFile $appSettingsPath $appSettings
$preserved['app/appsettings.json'] = Get-AppFileHash $appSettingsPath
$unknown = Join-Path $site app/Views/InstanceCreated.cshtml
Set-Content $unknown '@* authored in Umbraco *@'
# A shared definition edited on the site is refreshed; site content and site-added definitions are kept.
$sharedDefinition = Join-Path $site 'uSync/ContentTypes/webpage.config'
Add-Content $sharedDefinition '<!-- edited on the site -->'
$siteContent = Join-Path $site 'uSync/Content/sample-application.config'
Add-Content $siteContent '<!-- edited on the site -->'
$siteDefinition = Join-Path $site 'uSync/ContentTypes/siteowned.config'
Set-Content $siteDefinition '<ContentType Alias="siteOwned" />'
# A copy of a definition AppBlocks installs, as an older build or uSync's export on save leaves it, is tidied away.
$staleDefinitions = @('uSync/ContentTypes/appreport.config', 'uSync/ContentTypes/reportdefinition.config', 'uSync/DataTypes/ApplicationText.config') | ForEach-Object { Join-Path $site $_ }
foreach ($stale in $staleDefinitions) { Set-Content $stale '<!-- the export of an older build -->' }
# Updating to the same build exercises staging, preservation and the directory switch.
& (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root -WhatIf
foreach ($stale in $staleDefinitions) { Assert-True (Test-Path $stale) 'Update -WhatIf removed a definition.' }
& (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root
foreach ($path in $preserved.Keys) { Assert-True ((Get-AppFileHash (Join-Path $site $path)) -eq $preserved[$path]) "Update changed $path" }
Assert-True (Test-Path $unknown) 'Update lost an Umbraco-authored template.'
Assert-True ((Get-BinaryHash $sharedDefinition) -eq (Get-BinaryHash (Join-Path $published.Root 'app/uSync/ContentTypes/webpage.config'))) 'Update did not refresh a shared uSync definition.'
Assert-True ((Get-Content $siteContent -Raw) -like '*edited on the site*') 'Update overwrote site content.'
Assert-True (Test-Path $siteDefinition) 'Update removed a site-added definition.'
foreach ($stale in $staleDefinitions) { Assert-True (!(Test-Path $stale)) "Update left a stale copy of an AppBlocks definition: $stale" }
Assert-True (Test-Path (Join-Path $site 'uSync/DataTypes/ApplicationApplicationBlocks.config')) 'Update removed a definition the export keeps on purpose.'
foreach ($relative in $script:AppBlocksDefinitions) { Assert-True (!(Test-Path (Join-Path $published.Root "app/uSync/$relative"))) "The build still ships $relative, which AppBlocks installs." }
$updatedState = Read-JsonFile (Join-Path $site '.apphost/instance.json')
Assert-True ($updatedState.build -eq $published.Manifest.id -and $updatedState.assembly -eq $published.Manifest.assembly) 'Update did not record the installed build and its application.'
# The updated site starts on the data it had and serves what it served before.
$running = Start-TestSite $site $updatedState.assembly
try {
    $response = Wait-TestSite $running 'Sample Application'
    Assert-True ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Sample Application') "The updated site did not render its home. Logs: $site/app/umbraco/Logs"
    $signIn = Invoke-WebRequest "$($running.Base)/orders-form/" -SkipHttpErrorCheck
    Assert-True ($signIn.StatusCode -eq 200 -and $signIn.Content -match 'loginModel.Username') 'The updated site did not offer the member sign-in form on /orders-form/.'
    $media = Invoke-WebRequest "$($running.Base)/media/preserved.txt" -SkipHttpErrorCheck
    Assert-True ($media.StatusCode -eq 200) 'The updated site does not serve the media it had.'
    Write-Host 'PASS: the updated site starts on its data and serves its pages and media.'
} finally { Stop-TestSite $running 'after-update' }
# A release that changes a shared definition: the update delivers the file, and the site imports it at its next start,
# before it serves a page. The second build needs the checkout, where one dictionary text is changed while it publishes.
$dictionary = Join-Path $scripts 'src/UmbracoAppHost/uSync/Dictionary/app-skip.config'
if (!$Build -and (Test-Path -LiteralPath $dictionary)) {
    # The placeholder above is not a definition uSync can import, which the site's next start would try with the rest.
    Remove-Item -LiteralPath $siteDefinition
    $original = [IO.File]::ReadAllBytes($dictionary)
    try {
        $text = [IO.File]::ReadAllText($dictionary)
        Assert-True ($text.Contains('>Skip to content<')) "The dictionary item this test changes no longer reads 'Skip to content': $dictionary"
        [IO.File]::WriteAllText($dictionary, $text.Replace('>Skip to content<', '>Skip to content, release test<'))
        $release = Publish-Reference $scripts '' '' $MsBuildProperties
    } finally { [IO.File]::WriteAllBytes($dictionary, $original) }
    if ($recordsSources) { Assert-True (!$release.Manifest.committed) 'A build made from an edited checkout says it was made from committed files.' }
    & (Join-Path $scripts update-site.ps1) -Site $site -Build $release.Root
    $releasedState = Read-JsonFile (Join-Path $site '.apphost/instance.json')
    Assert-True ($releasedState.build -eq $release.Manifest.id) 'The release update did not record its build.'
    Assert-True ((Get-Content (Join-Path $site 'uSync/Dictionary/app-skip.config') -Raw) -like '*release test*') 'The release update did not deliver the changed definition.'
    $running = Start-TestSite $site $releasedState.assembly
    try {
        $response = Wait-TestSite $running 'Skip to content, release test'
        Assert-True ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Skip to content, release test') "The site did not import the release's shared definition at its start. Logs: $site/app/umbraco/Logs"
        Write-Host 'PASS: a release that changes a shared definition reaches the site at its first start, with no step in the backoffice.'
    } finally { Stop-TestSite $running 'after-release' }
} else { Write-Host 'SKIPPED: the release update, which publishes a second build from the checkout.' }
$shared = Join-Path $site app/Views/WebsiteHome.cshtml
Add-Content $shared '@* local modification *@'
$rejected = $false
try { & (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root } catch { $rejected = $_.Exception.Message -like '*installed application was changed*' }
Assert-True $rejected 'Update overwrote a modified shared template.'
Write-Host "PASS: sites share identical application files; update preserves data, settings, media and content, refreshes shared definitions, removes stale copies of AppBlocks' definitions, and rejects conflicting edits. Test sites: $OutputRoot"
