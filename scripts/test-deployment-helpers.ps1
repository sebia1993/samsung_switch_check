Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-DeploymentTest {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )
    foreach ($needle in $Needles) {
        Assert-DeploymentTest -Condition $Text.Contains($needle) -Message "$Name contract is missing: $needle"
    }
}

$install = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-agent.ps1') -Raw -Encoding UTF8
$launcher = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-or-Update-Agent.cmd') -Raw -Encoding UTF8
$viewerLauncher = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-or-Update-Viewer.cmd') -Raw -Encoding UTF8
$uninstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'uninstall-agent.ps1') -Raw -Encoding UTF8
$viewerInstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-viewer.ps1') -Raw -Encoding UTF8
$mockSmoke = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'smoke-mock-agent.ps1') -Raw -Encoding UTF8
$build = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-release.ps1') -Raw -Encoding UTF8
$common = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8

Write-SswStep 'Service-first install-or-update contract'
Assert-ContainsAll -Name 'Agent installer' -Text $install -Needles @(
    '[string]$SourceDirectory = $PSScriptRoot',
    '[string[]]$ClientManagementCidrs',
    '[string[]]$AllowedTargetCidrs',
    '[switch]$Preflight',
    'https://0.0.0.0:18443',
    'AllowedTargetCidrs = @($TargetCidrs)',
    'MaxConcurrentExecutions = 2',
    'RateLimitPerMinute = 60',
    'MaxRequestBodyBytes = 32768',
    'MaxOutputBytes = 65536',
    'MaxSessionSeconds = 240',
    'Assert-SswAdministrator',
    'Get-SswAgentServiceName',
    '--service',
    'obj= NT AUTHORITY\LocalService',
    'Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute',
    'Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify',
    'Set-SswInstallerBackupAcl -Path $existingLegacyArchive.FullName',
    'Set-SswInstallerBackupAcl -Path $legacyArchive',
    'restart/5000/restart/15000/restart/60000',
    'Stop existing Agent service',
    'Back up persistent Agent identity and configuration data',
    'Atomically swap Agent program files',
    'Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60 -UseHttps',
    'Archive legacy Agent-owned credentials, database, and raw history',
    'legacy-v0.7-backup-{0}-{1}',
    "Join-Path `$programBackup 'appsettings.Production.json'",
    "Join-Path `$legacyArchive 'legacy-appsettings.Production.json'",
    "purpose = 'manual recovery or administrator-approved cleanup only'",
    'Restore-SswAgentFirewallSnapshots',
    'rollback-completed',
    'receiptVersion = 3',
    'clientManagementCidrs = @($clientCidrs)',
    'allowedTargetCidrs = @($targetCidrs)'
    'Get-SswLegacyBackgroundState'
    'Test-SswOwnedLegacyBackgroundTask'
    'Assert-SswBackgroundAgentReceipt'
    '$actualLegacyExeHash'
    '$runnerManifestEntries'
    "PSObject.Properties['configurationSha256']"
    '$configuration.Agent.PSObject.Properties[''Switches'']'
    'https-certificate.pfx.dpapi'
    'Get-SswDirectoryAclSnapshot'
    'Restore-SswDirectoryAclSnapshot'
    'Stop and unregister exact owned current-user Agent task'
    'Unregister-ScheduledTask -TaskName $legacyBackgroundTaskName'
    'Register-ScheduledTask -TaskName $legacyBackgroundTaskName'
    'legacy-background-backup-{0}-{1}'
    'Set-SswInstallerBackupAcl -Path $legacyBackgroundArchive'
    'legacyBackgroundTaskMigrated'
)
Assert-DeploymentTest -Condition ($install -notmatch '(?i)password|credentialId|switchesJsonPath|EnableReadOnlyQueries') `
    -Message 'Agent installer must not own switch credentials, inventory, or command opt-in state.'
Assert-ContainsAll -Name 'Restricted ProgramData ACL' -Text $common -Needles @(
    "SecurityIdentifier('S-1-5-18')",
    "SecurityIdentifier('S-1-5-32-544')",
    '$acl.SetAccessRuleProtection($true, $false)',
    '$acl.PurgeAccessRules($identity)',
    '$allowedSids = @($systemSid.Value, $administratorsSid.Value, $agentSid.Value)',
    '$unexpected.Count -gt 0',
    '$invalidChildRule'
)
$backupAclStart = $common.IndexOf('function Set-SswInstallerBackupAcl')
$backupAclEnd = $common.IndexOf('function Write-SswOperationJournal', $backupAclStart)
Assert-DeploymentTest -Condition ($backupAclStart -ge 0 -and $backupAclEnd -gt $backupAclStart) `
    -Message 'Installer backup ACL function block was not found.'
$backupAclBlock = $common.Substring($backupAclStart, $backupAclEnd - $backupAclStart)
Assert-ContainsAll -Name 'Installer backup ACL' -Text $backupAclBlock -Needles @(
    '$descendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)',
    '$childAcl.SetAccessRuleProtection($true, $false)',
    '$childAcl.PurgeAccessRules($identity)',
    '$childAcl.SetAccessRuleProtection($false, $false)',
    '$allowedSids = @($systemSid.Value, $administratorsSid.Value)',
    '$invalidChildRule'
)
Assert-DeploymentTest -Condition ($backupAclBlock -notmatch '(?i)ServiceSid|agentSid|S-1-5-80-') `
    -Message 'Installer backup ACL must not grant the Agent service SID.'
$dataAclIndex = $install.IndexOf(
    'Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify')
$legacyArchiveAclIndex = $install.IndexOf('Set-SswInstallerBackupAcl -Path $legacyArchive', $dataAclIndex)
Assert-DeploymentTest -Condition ($dataAclIndex -ge 0 -and $legacyArchiveAclIndex -gt $dataAclIndex) `
    -Message 'Legacy archive ACL must be applied after the active data-directory ACL.'

Write-SswStep 'Simple UAC launcher and package contract'
Assert-ContainsAll -Name 'UAC launcher' -Text $launcher -Needles @(
    'install-agent.ps1',
    'Start-Process',
    '-Verb RunAs',
    '-Wait',
    'SSW_INSTALLER_PATH',
    '-EncodedCommand',
    'Read-Host',
    'pause'
)
Assert-DeploymentTest -Condition (
    $build -match "\[string\]\`$Version\s*=\s*'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?'") `
    -Message 'Release build default must be a semantic version.'
Assert-DeploymentTest -Condition $build.Contains("'Install-or-Update-Agent.cmd'") `
    -Message 'Agent package must include the one-click UAC launcher.'
Assert-ContainsAll -Name 'Viewer launcher' -Text $viewerLauncher -Needles @(
    'install-viewer.ps1',
    'powershell.exe',
    '-StartWithWindows',
    'pause'
)
Assert-DeploymentTest -Condition (-not $viewerLauncher.Contains('-Verb RunAs')) `
    -Message 'Per-user Viewer launcher must not request administrator elevation.'
Assert-DeploymentTest -Condition $build.Contains("'Install-or-Update-Viewer.cmd'") `
    -Message 'Viewer package must include the one-click per-user launcher.'
Assert-DeploymentTest -Condition $build.Contains("'docs\SamsungSwitchWatch_User_Manual_KO.pdf'") `
    -Message 'Both release packages must include the final PDF user manual.'
Assert-DeploymentTest -Condition (-not $build.Contains('SamsungSwitchWatch_User_Manual_KO.docx')) `
    -Message 'Editable DOCX manual must remain outside release packages.'
Assert-DeploymentTest -Condition (
    $build.Contains("Where-Object { `$_.Name -ne 'SamsungSwitchWatch.Agent.exe' }") -and
    $build.Contains('Remove-Item -Force')
) -Message 'Public Agent package must discard every non-EXE publish byproduct before adding the service payload.'
foreach ($legacyBackgroundScript in @(
    'install-agent-background.ps1',
    'run-agent-background.ps1',
    'uninstall-agent-background.ps1'
)) {
    Assert-DeploymentTest -Condition (-not $build.Contains("'$legacyBackgroundScript'")) `
        -Message "Public Agent package must be service-only: $legacyBackgroundScript"
}
foreach ($removed in @('set-switch-credential.ps1', 'set-viewer-access.ps1', 'switches.example.json')) {
    Assert-DeploymentTest -Condition (-not $build.Contains("'$removed'")) `
        -Message "Obsolete Agent-owned configuration helper is still packaged: $removed"
}

Write-SswStep 'Viewer startup shortcut preservation contract'
Assert-ContainsAll -Name 'Viewer installer' -Text $viewerInstall -Needles @(
    '[switch]$StartWithWindows',
    '[switch]$DisableStartWithWindows',
    "if (`$StartWithWindows -and `$DisableStartWithWindows)",
    'if ($StartWithWindows) { Copy-Item -LiteralPath $startMenu -Destination $startup -Force }',
    'elseif ($DisableStartWithWindows -and (Test-Path -LiteralPath $startup -PathType Leaf))'
)
Assert-DeploymentTest -Condition (
    $mockSmoke.Contains("-ArgumentList '--service'") -and
    -not $mockSmoke.Contains("-ArgumentList '--background'")
) -Message 'Mock smoke test must exercise the service-only Agent runtime.'

Write-SswStep 'Viewer transactional rollback and commit boundary contract'
Assert-ContainsAll -Name 'Viewer transaction boundary' -Text $viewerInstall -Needles @(
    '$shortcutBackupsReady = $false',
    '$shortcutMutationStarted = $false',
    '$transactionCommitted = $false',
    '$shortcutBackupsReady = $true',
    '$shortcutMutationStarted = $true',
    'if (-not $shortcutMutationStarted) { return }',
    'if (-not $shortcutBackupsReady) { throw',
    'if ($transactionCommitted) {',
    "Name = 'cleanup-program-backup'",
    "Name = 'cleanup-shortcut-backup'"
)
$shortcutBackupReadyIndex = $viewerInstall.IndexOf('$shortcutBackupsReady = $true')
$shortcutMutationIndex = $viewerInstall.IndexOf('$shortcutMutationStarted = $true')
Assert-DeploymentTest -Condition (
    $shortcutBackupReadyIndex -ge 0 -and
    $shortcutMutationIndex -gt $shortcutBackupReadyIndex
) -Message 'Viewer shortcut mutation must begin only after every previous shortcut is backed up.'

$viewerCommitIndex = $viewerInstall.IndexOf("-Stage 'completed' -Status 'succeeded'")
$viewerCommittedFlagIndex = $viewerInstall.IndexOf(
    '$transactionCommitted = $true',
    $viewerCommitIndex)
$viewerProgramCleanupIndex = $viewerInstall.IndexOf(
    "Name = 'cleanup-program-backup'",
    $viewerCommittedFlagIndex)
Assert-DeploymentTest -Condition (
    $viewerCommitIndex -ge 0 -and
    $viewerCommittedFlagIndex -gt $viewerCommitIndex -and
    $viewerProgramCleanupIndex -gt $viewerCommittedFlagIndex
) -Message 'Viewer must durably commit before deleting the previous program backup.'

$viewerCatchIndex = $viewerInstall.IndexOf('catch {', $viewerProgramCleanupIndex)
$viewerCommittedCatchIndex = $viewerInstall.IndexOf(
    'if ($transactionCommitted) {',
    $viewerCatchIndex)
$viewerRollbackPlanIndex = $viewerInstall.IndexOf(
    '$rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(',
    $viewerCatchIndex)
Assert-DeploymentTest -Condition (
    $viewerCatchIndex -ge 0 -and
    $viewerCommittedCatchIndex -gt $viewerCatchIndex -and
    $viewerRollbackPlanIndex -gt $viewerCommittedCatchIndex
) -Message 'Viewer post-commit failures must not enter the pre-commit rollback plan.'

Write-SswStep 'Operation journal cleanup is best effort'
Assert-ContainsAll -Name 'Operation journal cleanup' -Text $common -Needles @(
    'function Remove-SswOperationJournalArtifactBestEffort',
    'Remove-SswOperationJournalArtifactBestEffort -Path $temporary',
    'Remove-SswOperationJournalArtifactBestEffort -Path $replaceBackup'
)
Assert-DeploymentTest -Condition (
    -not $common.Contains(
        'if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }')
) -Message 'Journal replacement backup cleanup must not throw after a durable commit.'
$journalHelperIndex = $common.IndexOf('function Remove-SswOperationJournalArtifactBestEffort')
$journalWriterIndex = $common.IndexOf('function Write-SswOperationJournal')
$journalCleanupTryIndex = $common.IndexOf('try {', $journalHelperIndex)
$journalCleanupProbeIndex = $common.IndexOf(
    'if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }',
    $journalHelperIndex)
Assert-DeploymentTest -Condition (
    $journalHelperIndex -ge 0 -and
    $journalCleanupTryIndex -gt $journalHelperIndex -and
    $journalCleanupProbeIndex -gt $journalCleanupTryIndex -and
    $journalWriterIndex -gt $journalCleanupProbeIndex
) -Message 'The full journal artifact probe and deletion must be best effort before journal writes.'

$journalTestRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-deployment-helper-{0}' -f [Guid]::NewGuid().ToString('N'))
$lockedJournalArtifact = Join-Path $journalTestRoot 'locked-journal-artifact.tmp'
$lockedJournalHandle = $null
try {
    New-Item -ItemType Directory -Path $journalTestRoot | Out-Null
    [IO.File]::WriteAllText($lockedJournalArtifact, 'locked', (New-Object Text.UTF8Encoding($false)))
    $lockedJournalHandle = [IO.File]::Open(
        $lockedJournalArtifact,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    Remove-SswOperationJournalArtifactBestEffort -Path $lockedJournalArtifact
    Assert-DeploymentTest -Condition (Test-Path -LiteralPath $lockedJournalArtifact -PathType Leaf) `
        -Message 'A locked journal artifact must be preserved without failing the completed operation.'
    $lockedJournalHandle.Dispose()
    $lockedJournalHandle = $null
    Remove-SswOperationJournalArtifactBestEffort -Path $lockedJournalArtifact
    Assert-DeploymentTest -Condition (-not (Test-Path -LiteralPath $lockedJournalArtifact)) `
        -Message 'An unlocked journal artifact must be removed by best-effort cleanup.'
}
finally {
    if ($lockedJournalHandle) { $lockedJournalHandle.Dispose() }
    if (Test-Path -LiteralPath $journalTestRoot -PathType Container) {
        Remove-Item -LiteralPath $journalTestRoot -Recurse -Force
    }
}

$shortcutBackupValidationIndex = $viewerInstall.IndexOf('$requiredShortcutBackups = @()')
$shortcutRemovalIndex = $viewerInstall.IndexOf(
    'foreach ($link in @($startMenu, $startup))',
    $shortcutBackupValidationIndex)
Assert-DeploymentTest -Condition (
    $shortcutBackupValidationIndex -ge 0 -and
    $shortcutRemovalIndex -gt $shortcutBackupValidationIndex
) -Message 'Viewer rollback must validate every required shortcut backup before removing current links.'

Write-SswStep 'CIDR canonicalization'
$normalized = @(ConvertTo-SswIpv4Cidrs -Cidr @('10.20.30.9/24', '10.20.30.0/24', '10.40.0.10/32'))
Assert-DeploymentTest -Condition (($normalized -join ',') -eq '10.20.30.0/24,10.40.0.10/32') `
    -Message 'IPv4 CIDR normalization or duplicate removal failed.'
foreach ($invalid in @('10.20.30.0', '010.20.30.0/24', '10.20.30.256/24', '10.20.30.0/33', 'LocalSubnet')) {
    $rejected = $false
    try { $null = ConvertTo-SswIpv4Cidrs -Cidr @($invalid) } catch { $rejected = $true }
    Assert-DeploymentTest -Condition $rejected -Message "Invalid CIDR was accepted: $invalid"
}

Write-SswStep 'HTTPS firewall snapshot contract'
$snapshot = [pscustomobject]@{
    Name = 'SamsungSwitchWatchAgent-Https'
    DisplayName = 'Samsung Switch Watch Agent HTTPS'
    Group = 'Samsung Switch Watch'
    Description = 'Owned by SamsungSwitchWatchAgent installer v3'
    Enabled = 'True'
    Direction = 'Inbound'
    Action = 'Allow'
    Protocol = 'TCP'
    LocalPort = '18443'
    RemotePort = 'Any'
    LocalAddress = @('Any')
    RemoteAddress = @('10.20.30.0/24')
    Program = 'Any'
    Service = 'Any'
    InterfaceType = 'Any'
    Profile = 'Domain, Private'
}
Assert-DeploymentTest -Condition (Test-SswAgentHttpsFirewallRuleExact -Snapshot $snapshot `
    -RemoteAddress @('10.20.30.9/24')) -Message 'Exact HTTPS firewall rule was rejected.'
$snapshot.LocalPort = '18444'
Assert-DeploymentTest -Condition (-not (Test-SswAgentHttpsFirewallRuleExact -Snapshot $snapshot `
    -RemoteAddress @('10.20.30.0/24'))) -Message 'Wrong HTTPS port was accepted.'

Write-SswStep 'Uninstall ownership and data preservation contract'
Assert-ContainsAll -Name 'Agent uninstaller' -Text $uninstall -Needles @(
    'Assert-SswAgentExecutorReceipt',
    'Remove-SswOwnedAgentFirewallRuleByName',
    '[switch]$RemoveData',
    'Agent identity and configuration data were preserved'
)

Write-SswStep 'Deployment helper contract passed'
