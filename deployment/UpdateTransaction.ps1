# Called under the instance run lock. The staged inventory is the last rename and records
# which update committed, including when the operator installs the same build twice.
function Restore-UpdateDirectory([string] $Previous, [string] $Live, [string] $Staged) {
    # The stop-point comments mark where deployment/test-update-interruptions.ps1 stops a copy of this file.
    if ([IO.Directory]::Exists($Previous)) {
        if ([IO.Directory]::Exists($Live)) {
            [IO.Directory]::Move($Live, $Staged)
            # stop-point: restore-live-set-aside
        }
        [IO.Directory]::Move($Previous, $Live)
        # stop-point: restore-previous-returned
    }
}

function Resolve-PendingUpdate([string] $SiteRoot) {
    $pending = Get-SafePath $SiteRoot '.apphost/pending-update'
    if (![IO.Directory]::Exists($pending)) { return }
    $plan = Read-JsonFile (Get-SafePath $pending 'plan.json')
    if ($plan.id -cnotmatch '^[0-9a-f]{32}$' -or $plan.backup -notmatch '^[\w.-]+$' -or !$plan.backup.EndsWith('-' + $plan.id)) {
        throw "Invalid update record in $pending"
    }
    $state = Read-JsonFile (Get-SafePath $SiteRoot '.apphost/instance.json')
    if ($state['updateId'] -ne $plan.id) {
        $sync = Get-SafePath $SiteRoot uSync
        $stagedSync = Get-SafePath $pending uSync
        if ($plan.hadSync) {
            Restore-UpdateDirectory (Get-SafePath $pending 'previous-uSync') $sync $stagedSync
        } elseif (![IO.Directory]::Exists($stagedSync) -and [IO.Directory]::Exists($sync)) {
            [IO.Directory]::Move($sync, $stagedSync)
            # stop-point: restore-usync-set-aside
        }
        Restore-UpdateDirectory (Get-SafePath $SiteRoot ('.apphost/backups/' + $plan.backup)) (Get-SafePath $SiteRoot app) (Get-SafePath $pending app)
        Write-Host 'Restored the previous application, uSync folder and installed build record.'
    }
    # Retire the journal atomically. An interruption during cleanup leaves only scratch files.
    $scratch = Get-SafePath $SiteRoot '.apphost/preparing-update'
    [IO.Directory]::Move($pending, $scratch)
    # stop-point: journal-retired
    try { Remove-Item -LiteralPath $scratch -Recurse -Force }
    catch { Write-Warning "The site inventory and files agree. The next update will retry removing the scratch folder at $scratch. $($_.Exception.Message)" }
}
