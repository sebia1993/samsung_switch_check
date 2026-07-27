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
$viewerUninstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'uninstall-viewer.ps1') -Raw -Encoding UTF8
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
    '$virtualServiceAccount = "NT SERVICE\$serviceName"',
    "'obj=' `$virtualServiceAccount",
    'Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute',
    'Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid',
    '-ServiceRights Modify -AllowServiceOwnedDescendants',
    '-AllowLegacyLocalServiceOwnedDescendants:$previousServiceUsesLocalService',
    'Set-SswInstallerBackupAcl -Path $existingLegacyArchive.FullName',
    'Set-SswInstallerBackupAcl -Path $legacyArchive',
    'restart/5000/restart/15000/restart/60000',
    'Stop existing Agent service',
    'Secure and back up persistent Agent identity and configuration data',
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
    'Assert-SswTrustedDirectoryRootOwner -Path $install'
    'Assert-SswTrustedDirectoryRootOwner -Path $data'
    'Set-SswAdministratorsOnlyFileAcl -Path $receiptPath'
    'Assert-SswLegacyBackgroundRollbackReadyForDataRestore'
)
Assert-DeploymentTest -Condition ($install -notmatch '(?i)password|credentialId|switchesJsonPath|EnableReadOnlyQueries') `
    -Message 'Agent installer must not own switch credentials, inventory, or command opt-in state.'
Assert-ContainsAll -Name 'Windows service command tokenization' -Text $install -Needles @(
    '$serviceBinPathForSc = ''\"'' + $installedExe + ''\" --service''',
    "& sc.exe create `$serviceName 'binPath=' `$serviceBinPathForSc 'start=' 'auto'",
    "'obj=' `$virtualServiceAccount",
    "'DisplayName=' 'Samsung Switch Watch Agent'",
    "& sc.exe config `$serviceName 'binPath=' `$serviceBinPathForSc 'start=' 'auto'",
    "& sc.exe failure `$serviceName 'reset=' '86400'",
    "'actions=' 'restart/5000/restart/15000/restart/60000'",
    '$oldPathForSc = $oldPath.Replace(''"'', ''\"'')',
    "& sc.exe config `$serviceName 'binPath=' `$oldPathForSc 'start=' `$oldStartTypeForSc",
    "'obj=' `$oldStartName",
    '$expectedServicePath = "`"$installedExe`" --service"',
    '$appliedServiceConfiguration = Get-CimInstance Win32_Service',
    '[string]$appliedServiceConfiguration.PathName -cne $expectedServicePath',
    '[string]$appliedServiceConfiguration.StartName -ine $virtualServiceAccount',
    '[string]$appliedServiceConfiguration.StartMode -cne ''Auto'''
)
Assert-DeploymentTest -Condition (
    -not $install.Contains('"binPath= $serviceBinPath"') -and
    -not $install.Contains("'start= auto'") -and
    -not $install.Contains('"obj= $virtualServiceAccount"') -and
    -not $install.Contains("'reset= 86400'")
) -Message 'sc.exe option names and values must be separate argv tokens.'
Assert-ContainsAll -Name 'Direct SID ACL helpers' -Text $common -Needles @(
    'function Get-SswAclOwnerSid',
    '$Acl.GetOwner(',
    '[Security.Principal.SecurityIdentifier]).Value',
    'function Get-SswFileSystemAccessRulesBySid',
    'function Clear-SswFileSystemAccessRules'
)
$restrictedAclStart = $common.IndexOf('function Set-SswRestrictedDirectoryAcl')
$restrictedAclEnd = $common.IndexOf('function Set-SswInstallerBackupAcl', $restrictedAclStart)
Assert-DeploymentTest -Condition (
    $restrictedAclStart -ge 0 -and
    $restrictedAclEnd -gt $restrictedAclStart
) -Message 'Restricted ProgramData ACL function block was not found.'
$restrictedAclBlock = $common.Substring(
    $restrictedAclStart,
    $restrictedAclEnd - $restrictedAclStart)
Assert-ContainsAll -Name 'Restricted ProgramData ACL' -Text $restrictedAclBlock -Needles @(
    "SecurityIdentifier('S-1-5-18')",
    "SecurityIdentifier('S-1-5-32-544')",
    'Get-SswAclOwnerSid -Acl $acl',
    'Test-SswTrustedAgentDescendantOwnerSid',
    '$preflightDirectories = New-Object Collections.Generic.Queue[string]',
    '$preflightDirectories.Enqueue($resolved)',
    '$AllowServiceOwnedDescendants',
    '$AllowLegacyLocalServiceOwnedDescendants',
    'AGENT_DIRECTORY_TRUST_INVALID',
    '$acl.SetOwner($administratorsSid)',
    '$acl.SetAccessRuleProtection($true, $false)',
    'Clear-SswFileSystemAccessRules -Acl $acl',
    'Set-Acl -LiteralPath $resolved -AclObject $acl',
    '$pendingDirectories = New-Object Collections.Generic.Queue[string]',
    '$pendingDirectories.Enqueue($resolved)',
    'Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop',
    '$childAcl.SetOwner($administratorsSid)',
    'Clear-SswFileSystemAccessRules -Acl $childAcl',
    '$allowedSids = @($systemSid.Value, $administratorsSid.Value, $agentSid.Value)',
    '$unexpected.Count -gt 0',
    '$verifiedDescendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)',
    '$invalidChildRule'
)
$restrictedRootAclWriteIndex = $restrictedAclBlock.IndexOf(
    'Set-Acl -LiteralPath $resolved -AclObject $acl')
$restrictedPreflightIndex = $restrictedAclBlock.IndexOf(
    '$preflightDirectories.Enqueue($resolved)')
$restrictedChildQueueIndex = $restrictedAclBlock.IndexOf(
    '$pendingDirectories.Enqueue($resolved)')
$restrictedFirstChildReadIndex = $restrictedAclBlock.IndexOf(
    'Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop',
    $restrictedChildQueueIndex)
$restrictedFinalTreeIndex = $restrictedAclBlock.IndexOf('$verifiedDescendants = @(')
Assert-DeploymentTest -Condition (
    $restrictedPreflightIndex -ge 0 -and
    $restrictedRootAclWriteIndex -gt $restrictedPreflightIndex -and
    $restrictedChildQueueIndex -gt $restrictedRootAclWriteIndex -and
    $restrictedFirstChildReadIndex -gt $restrictedChildQueueIndex -and
    $restrictedFinalTreeIndex -gt $restrictedFirstChildReadIndex
) -Message 'Restricted ProgramData ACL must secure the root before enumerating children and then re-enumerate for final verification.'
Assert-DeploymentTest -Condition (
    $restrictedAclBlock.IndexOf(
        'Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop') -gt
    $restrictedRootAclWriteIndex
) -Message 'Restricted ProgramData ACL must not recursively enumerate descendants before securing the root.'
$backupAclStart = $common.IndexOf('function Set-SswInstallerBackupAcl')
$backupAclEnd = $common.IndexOf('function Initialize-SswAgentOperationsRoot', $backupAclStart)
Assert-DeploymentTest -Condition ($backupAclStart -ge 0 -and $backupAclEnd -gt $backupAclStart) `
    -Message 'Installer backup ACL function block was not found.'
$backupAclBlock = $common.Substring($backupAclStart, $backupAclEnd - $backupAclStart)
Assert-ContainsAll -Name 'Installer backup ACL' -Text $backupAclBlock -Needles @(
    '[switch]$ValidateExistingOwner',
    '$ownerTrustCache = @{}',
    '$ownerTrustCache.ContainsKey($ownerSid)',
    'Test-SswTrustedAdministrativeOwnerSid -Sid $ownerSid',
    '$preflightDirectories = New-Object Collections.Generic.Queue[string]',
    '$preflightDirectories.Enqueue($resolved)',
    '$pendingDirectories = New-Object Collections.Generic.Queue[string]',
    '$pendingDirectories.Enqueue($resolved)',
    'Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop',
    '$childAcl.SetAccessRuleProtection($true, $false)',
    'Clear-SswFileSystemAccessRules -Acl $childAcl',
    '$childAcl.SetAccessRuleProtection($false, $false)',
    '$allowedSids = @($systemSid.Value, $administratorsSid.Value)',
    'Get-SswAclOwnerSid -Acl $acl',
    '$acl.SetOwner($administratorsSid)',
    '$childAcl.SetOwner($administratorsSid)',
    '$verifiedDescendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)',
    '$invalidChildRule'
)
Assert-DeploymentTest -Condition ($backupAclBlock -notmatch '(?i)ServiceSid|agentSid|S-1-5-80-') `
    -Message 'Installer backup ACL must not grant the Agent service SID.'
$rootAclWriteIndex = $backupAclBlock.IndexOf('Set-Acl -LiteralPath $resolved -AclObject $acl')
$backupPreflightIndex = $backupAclBlock.IndexOf('$preflightDirectories.Enqueue($resolved)')
$childQueueIndex = $backupAclBlock.IndexOf('$pendingDirectories.Enqueue($resolved)')
$finalTreeIndex = $backupAclBlock.IndexOf('$verifiedDescendants = @(')
Assert-DeploymentTest -Condition (
    $backupPreflightIndex -ge 0 -and
    $rootAclWriteIndex -gt $backupPreflightIndex -and
    $childQueueIndex -gt $rootAclWriteIndex -and
    $finalTreeIndex -gt $childQueueIndex
) -Message 'Installer backup ACL must secure the root before enumerating children and then re-enumerate for final verification.'
Assert-DeploymentTest -Condition (
    (Test-SswTrustedAdministrativeOwnerSid -Sid 'S-1-5-18') -and
    (Test-SswTrustedAdministrativeOwnerSid -Sid 'S-1-5-32-544') -and
    -not (Test-SswTrustedAdministrativeOwnerSid -Sid 'S-1-1-0')
) -Message 'Operations owner migration must accept SYSTEM/Administrators and reject Everyone.'
$dataAclIndex = $install.IndexOf(
    'Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid')
$legacyArchiveAclIndex = $install.IndexOf('Set-SswInstallerBackupAcl -Path $legacyArchive', $dataAclIndex)
Assert-DeploymentTest -Condition ($dataAclIndex -ge 0 -and $legacyArchiveAclIndex -gt $dataAclIndex) `
    -Message 'Legacy archive ACL must be applied after the active data-directory ACL.'
$dataCreateIndex = $install.IndexOf(
    'New-Item -ItemType Directory -Path $data -ErrorAction Stop')
$dataCreatedFlagIndex = $install.IndexOf('$dataCreated = $true', $dataCreateIndex)
$legacyIdentityCopyIndex = $install.IndexOf(
    'Copy-Item -LiteralPath $legacyBackgroundState.IdentityMetadataPath')
Assert-DeploymentTest -Condition (
    $dataCreateIndex -ge 0 -and
    $dataAclIndex -gt $dataCreateIndex -and
    $dataCreatedFlagIndex -gt $dataAclIndex -and
    $legacyIdentityCopyIndex -gt $dataAclIndex
) -Message 'New Agent data must be created without -Force, secured before rollback ownership is claimed, and locked before persistent HTTPS identity is copied.'
Assert-DeploymentTest -Condition (
    -not $install.Contains('New-Item -ItemType Directory -Path $data -Force')
) -Message 'Agent installation must not adopt a raced data directory through New-Item -Force.'
Assert-DeploymentTest -Condition (
    $install.Contains(
        "-ProductRelativeRoot 'SamsungSwitchWatch' -RequireExactProductRoot") -and
    $install.Contains('Refusing to adopt even an empty pre-existing directory.')
) -Message 'Agent installation must use only the exact ProgramData product root and reject empty preclaims.'
$stageAclIndex = $install.IndexOf('Set-SswInstallerBackupAcl -Path $staging')
$stageCopyIndex = $install.IndexOf(
    'Copy-Item -LiteralPath (Join-Path $source ([string]$file.name))')
$stageRehashIndex = $install.IndexOf(
    '$stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256)')
$stageMoveIndex = $install.IndexOf('Move-Item -LiteralPath $staging -Destination $install')
Assert-DeploymentTest -Condition (
    $stageAclIndex -ge 0 -and
    $stageCopyIndex -gt $stageAclIndex -and
    $stageRehashIndex -gt $stageCopyIndex -and
    $stageMoveIndex -gt $stageRehashIndex
) -Message 'Agent package must be copied into Administrators-only staging and re-hashed before the Program Files swap.'
$receiptTrustIndex = $install.IndexOf(
    'Test-SswAdministratorsOnlyFileAcl -Path $receiptPath')
$receiptReadIndex = $install.IndexOf(
    'Read-SswJson -Path $receiptPath -Label ''Installed Agent receipt''')
$configTargetIndex = $install.IndexOf(
    '@($existingConfig.Agent.AllowedTargetCidrs)')
$firewallClientIndex = $install.IndexOf(
    '$existingManagementFirewall.RemoteAddress')
Assert-DeploymentTest -Condition (
    $receiptTrustIndex -ge 0 -and
    $receiptReadIndex -gt $receiptTrustIndex -and
    $configTargetIndex -gt $receiptTrustIndex -and
    $firewallClientIndex -gt $receiptTrustIndex -and
    -not $install.Contains('$validatedReceipt.ClientManagementCidrs') -and
    -not $install.Contains('$validatedReceipt.AllowedTargetCidrs')
) -Message 'Service-writable receipt fields must not supply elevated CIDR policy; config and owned firewall state are authoritative.'
$receiptWriteIndex = $install.IndexOf(
    '[IO.File]::WriteAllText($temporaryReceipt, $receipt')
$receiptAclIndex = $install.IndexOf(
    'Set-SswAdministratorsOnlyFileAcl -Path $receiptPath',
    $receiptWriteIndex)
$serviceStartIndex = $install.IndexOf('Start-Service -Name $serviceName', $receiptWriteIndex)
Assert-DeploymentTest -Condition (
    $receiptWriteIndex -ge 0 -and
    $receiptAclIndex -gt $receiptWriteIndex -and
    $serviceStartIndex -gt $receiptAclIndex
) -Message 'The install receipt must be Administrators-only before the service can start.'
$rollbackGuardIndex = $install.IndexOf(
    'Assert-SswLegacyBackgroundRollbackReadyForDataRestore')
$rollbackDataQuarantineIndex = $install.IndexOf(
    'Restore-SswDirectoryWithQuarantine',
    $rollbackGuardIndex)
$rollbackCleanupGateIndex = $install.IndexOf(
    'if ($rollbackErrors.Count -eq 0)',
    $rollbackDataQuarantineIndex)
$rollbackArtifactLoopIndex = $install.IndexOf(
    'foreach ($rollbackArtifact in @($failedProgram, $transactionRoot))',
    $rollbackCleanupGateIndex)
$rollbackArtifactCleanupIndex = $install.IndexOf(
    'Remove-Item -LiteralPath $rollbackArtifact -Recurse -Force',
    $rollbackArtifactLoopIndex)
Assert-DeploymentTest -Condition (
    $rollbackGuardIndex -ge 0 -and
    $rollbackDataQuarantineIndex -gt $rollbackGuardIndex -and
    $rollbackCleanupGateIndex -gt $rollbackDataQuarantineIndex -and
    $rollbackArtifactLoopIndex -gt $rollbackCleanupGateIndex -and
    $rollbackArtifactCleanupIndex -gt $rollbackArtifactLoopIndex
) -Message 'Rollback must preserve active data until legacy restoration is complete and delete transaction snapshots only after an error-free plan.'
Assert-ContainsAll -Name 'Agent rollback dependency state' -Text $install -Needles @(
    '$rollbackState = [pscustomobject]@{',
    'ServiceQuiesced = $false',
    'ServiceRegistrationReady = $false',
    'ProgramRestored = $false',
    'ServiceConfigurationRestored = $false',
    'LegacyBackgroundFilesRestored = $false',
    'DataRestored = $false',
    'if (-not $rollbackState.ServiceQuiesced)',
    'if (-not $rollbackState.ProgramRestored)',
    'if (-not $rollbackState.ServiceConfigurationRestored)',
    '-not $rollbackState.LegacyBackgroundFilesRestored',
    'if ($serviceQuiescenceRequired -and $isUpdate)',
    'if ($serviceQuiescenceRequired -and $isUpdate -and',
    'Previous Agent state was not fully restored; service restart is blocked.'
)
Assert-ContainsAll -Name 'Agent program rollback disposition' -Text $install -Needles @(
    'Get-SswProgramRollbackDisposition',
    "'RestoreBackup'",
    "'QuarantineNewInstall'",
    "'AlreadyIntact'"
)
Assert-ContainsAll -Name 'Agent partial creation preservation' -Text $install -Needles @(
    '$dataCreationAttempted = $false',
    '$dataCreationAttempted = $true',
    'if ($dataCreationAttempted -and -not $dataCreated -and',
    '신규 Agent 데이터 폴더 생성 또는 ACL 적용 완료 여부가 불명확해 해당 폴더를 보존했습니다.'
)
$rollbackStopGuardIndex = $install.IndexOf(
    'if (-not $rollbackState.ServiceQuiesced -or')
$rollbackProgramQuarantineIndex = $install.IndexOf(
    'Restore-SswDirectoryWithQuarantine',
    $rollbackStopGuardIndex)
$rollbackDataDependencyIndex = $install.IndexOf(
    'Prior rollback dependencies are incomplete; active Agent data is preserved.')
$rollbackDependentDataQuarantineIndex = $install.IndexOf(
    'Restore-SswDirectoryWithQuarantine',
    $rollbackDataDependencyIndex)
Assert-DeploymentTest -Condition (
    $rollbackStopGuardIndex -ge 0 -and
    $rollbackProgramQuarantineIndex -gt $rollbackStopGuardIndex -and
    $rollbackDataDependencyIndex -gt $rollbackProgramQuarantineIndex -and
    $rollbackDependentDataQuarantineIndex -gt $rollbackDataDependencyIndex
) -Message 'A failed service stop or earlier rollback dependency must block program and data replacement.'
Assert-ContainsAll -Name 'Rollback source preflight and quarantine swap' -Text $install -Needles @(
    '$programBackupTaken = $true',
    'Set-SswInstallerBackupAcl -Path $programBackup',
    'Set-SswInstallerBackupAcl -Path $dataSnapshot',
    'Set-SswInstallerBackupAcl -Path $programBackup -ValidateExistingOwner',
    'Set-SswInstallerBackupAcl -Path $dataSnapshot -ValidateExistingOwner',
    '-ActivePath $install -BackupPath $programBackup',
    '-QuarantinePath $failedProgram -BackupRequired',
    'Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid',
    '-ServiceRights ReadAndExecute -AllowServiceOwnedDescendants',
    '-ActivePath $data -BackupPath $dataSnapshot',
    '-QuarantinePath $failedData -BackupRequired:$dataSnapshotTaken'
)

$legacyArchiveCreateIndex = $install.IndexOf(
    'New-Item -ItemType Directory -Path $legacyBackgroundArchive -ErrorAction Stop')
$legacyArchiveAclIndex = $install.IndexOf(
    'Set-SswInstallerBackupAcl -Path $legacyBackgroundArchive',
    $legacyArchiveCreateIndex)
$legacyProgramAttemptIndex = $install.IndexOf(
    '$legacyBackgroundProgramMoveAttempted = $true',
    $legacyArchiveAclIndex)
$legacyProgramMoveIndex = $install.IndexOf(
    'Move-Item -LiteralPath $legacyBackgroundState.InstallDirectory',
    $legacyProgramAttemptIndex)
$legacyDataAttemptIndex = $install.IndexOf(
    '$legacyBackgroundDataMoveAttempted = $true',
    $legacyProgramMoveIndex)
$legacyDataMoveIndex = $install.IndexOf(
    'Move-Item -LiteralPath $legacyBackgroundState.DataDirectory',
    $legacyDataAttemptIndex)
Assert-DeploymentTest -Condition (
    $legacyArchiveCreateIndex -ge 0 -and
    $legacyArchiveAclIndex -gt $legacyArchiveCreateIndex -and
    $legacyProgramAttemptIndex -gt $legacyArchiveAclIndex -and
    $legacyProgramMoveIndex -gt $legacyProgramAttemptIndex -and
    $legacyDataAttemptIndex -gt $legacyProgramMoveIndex -and
    $legacyDataMoveIndex -gt $legacyDataAttemptIndex
) -Message 'Legacy archive must be protected before any move and every move must be marked attempted before mutation.'
Assert-ContainsAll -Name 'Agent uninstaller directory ownership preflight' -Text $uninstall -Needles @(
    'Assert-SswTrustedDirectoryRootOwner -Path $install',
    'Assert-SswTrustedDirectoryRootOwner -Path $data',
    'Assert-SswAdministratorsOnlyFileAcl -Path $receiptPath',
    '-ProductRelativeRoot ''SamsungSwitchWatch'' -RequireExactProductRoot'
)
Assert-ContainsAll -Name 'Agent uninstaller destructive dependency gates' -Text $uninstall -Needles @(
    '$uninstallState = [pscustomobject]@{',
    'ServiceQuiesced = $false',
    'ServiceDeleted = $false',
    'if (-not $uninstallState.ServiceQuiesced)',
    'if (-not $uninstallState.ServiceDeleted)',
    'Service deletion was not confirmed; firewall removal is blocked.',
    'Service deletion was not confirmed; program removal is blocked.',
    'Service deletion was not confirmed; data removal is blocked.'
)
$uninstallDeleteGateIndex = $uninstall.IndexOf(
    'Service deletion was not confirmed; program removal is blocked.')
$uninstallDangerousProgramDeleteIndex = $uninstall.IndexOf(
    'Remove-Item -LiteralPath $install -Recurse -Force',
    $uninstallDeleteGateIndex)
$uninstallDataGateIndex = $uninstall.IndexOf(
    'Service deletion was not confirmed; data removal is blocked.')
$uninstallDangerousDataDeleteIndex = $uninstall.IndexOf(
    'Remove-Item -LiteralPath $data -Recurse -Force',
    $uninstallDataGateIndex)
Assert-DeploymentTest -Condition (
    $uninstallDeleteGateIndex -ge 0 -and
    $uninstallDangerousProgramDeleteIndex -gt $uninstallDeleteGateIndex -and
    $uninstallDataGateIndex -gt $uninstallDangerousProgramDeleteIndex -and
    $uninstallDangerousDataDeleteIndex -gt $uninstallDataGateIndex
) -Message 'Uninstall must not delete program or data until service deletion is confirmed.'
$uninstallRootTrustIndex = $uninstall.IndexOf(
    'Assert-SswTrustedDirectoryRootOwner -Path $install')
$uninstallConfigReadIndex = $uninstall.IndexOf(
    'Get-Content -LiteralPath $configPath -Raw -Encoding UTF8')
$uninstallProgramRemovalIndex = $uninstall.IndexOf(
    'Remove-Item -LiteralPath $install -Recurse -Force')
Assert-DeploymentTest -Condition (
    $uninstallRootTrustIndex -ge 0 -and
    $uninstallConfigReadIndex -gt $uninstallRootTrustIndex -and
    $uninstallProgramRemovalIndex -gt $uninstallRootTrustIndex
) -Message 'Agent uninstall must validate the install root before reading configuration or removing remnants.'

Write-SswStep 'Legacy background rollback archive preservation contract'
$legacyRollbackRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-legacy-rollback-{0}' -f [Guid]::NewGuid().ToString('N'))
$legacyRollbackArchive = Join-Path $legacyRollbackRoot 'archive'
$legacyProgramRestore = Join-Path $legacyRollbackRoot 'restored-program'
$legacyDataRestore = Join-Path $legacyRollbackRoot 'restored-data'
try {
    New-Item -ItemType Directory -Path (
        Join-Path $legacyRollbackArchive 'program'
    ), (
        Join-Path $legacyRollbackArchive 'data'
    ) -Force | Out-Null
    $legacyRollbackFailure = $null
    try {
        Assert-SswLegacyBackgroundRollbackReadyForDataRestore `
            -ArchivePath $legacyRollbackArchive `
            -ProgramWasMoved $true -ProgramRestorePath $legacyProgramRestore `
            -DataWasMoved $true -DataRestorePath $legacyDataRestore
    }
    catch { $legacyRollbackFailure = $_.Exception.Message }
    Assert-DeploymentTest -Condition (
        [string]$legacyRollbackFailure -like 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED:*'
    ) -Message 'Unresolved legacy archives must block active Agent data rollback.'

    Move-Item -LiteralPath (Join-Path $legacyRollbackArchive 'program') `
        -Destination $legacyProgramRestore
    Move-Item -LiteralPath (Join-Path $legacyRollbackArchive 'data') `
        -Destination $legacyDataRestore
    Assert-SswLegacyBackgroundRollbackReadyForDataRestore `
        -ArchivePath $legacyRollbackArchive `
        -ProgramWasMoved $true -ProgramRestorePath $legacyProgramRestore `
        -DataWasMoved $true -DataRestorePath $legacyDataRestore

    $partialMoveFailure = $null
    try {
        Assert-SswLegacyBackgroundRollbackReadyForDataRestore `
            -ArchivePath $legacyRollbackArchive `
            -ProgramMoveAttempted $true -ProgramWasMoved $false `
            -ProgramRestorePath $legacyProgramRestore `
            -DataMoveAttempted $false -DataWasMoved $false `
            -DataRestorePath $legacyDataRestore
    }
    catch { $partialMoveFailure = $_.Exception.Message }
    Assert-DeploymentTest -Condition (
        [string]$partialMoveFailure -like 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED:*'
) -Message 'Attempted-but-incomplete legacy moves must block active Agent data rollback.'
}
finally {
    Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $legacyRollbackRoot
    if (Test-Path -LiteralPath $legacyRollbackRoot) {
        Remove-Item -LiteralPath $legacyRollbackRoot -Recurse -Force
    }
}

Write-SswStep 'Directory restore source preflight and quarantine fixture'
$directoryRestoreRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-directory-restore-{0}' -f [Guid]::NewGuid().ToString('N'))
$directoryRestoreActive = Join-Path $directoryRestoreRoot 'active'
$directoryRestoreBackup = Join-Path $directoryRestoreRoot 'backup'
$directoryRestoreQuarantine = Join-Path $directoryRestoreRoot 'quarantine'
$directoryRestoreMissing = Join-Path $directoryRestoreRoot 'missing-backup'
try {
    New-Item -ItemType Directory -Path $directoryRestoreActive, $directoryRestoreBackup | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $directoryRestoreActive 'active.keep'),
        'active',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $directoryRestoreBackup 'backup.keep'),
        'backup',
        (New-Object Text.UTF8Encoding($false)))
    $quarantined = Restore-SswDirectoryWithQuarantine `
        -ActivePath $directoryRestoreActive -BackupPath $directoryRestoreBackup `
        -QuarantinePath $directoryRestoreQuarantine -BackupRequired
    Assert-DeploymentTest -Condition (
        $quarantined -and
        (Test-Path -LiteralPath (Join-Path $directoryRestoreActive 'backup.keep') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $directoryRestoreQuarantine 'active.keep') -PathType Leaf) -and
        -not (Test-Path -LiteralPath $directoryRestoreBackup)
    ) -Message 'A verified backup must replace active data only after the active directory is quarantined.'

    $missingSourceFailure = $null
    try {
        Restore-SswDirectoryWithQuarantine `
            -ActivePath $directoryRestoreActive -BackupPath $directoryRestoreMissing `
            -QuarantinePath (Join-Path $directoryRestoreRoot 'must-not-exist') `
            -BackupRequired
    }
    catch { $missingSourceFailure = $_.Exception.Message }
    Assert-DeploymentTest -Condition (
        [string]$missingSourceFailure -like 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED:*' -and
        (Test-Path -LiteralPath (Join-Path $directoryRestoreActive 'backup.keep') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $directoryRestoreRoot 'must-not-exist'))
    ) -Message 'A missing restore source must leave the active directory untouched.'
}
finally {
    Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $directoryRestoreRoot
    if (Test-Path -LiteralPath $directoryRestoreRoot) {
        Remove-Item -LiteralPath $directoryRestoreRoot -Recurse -Force
    }
}

Write-SswStep 'Program rollback pre-swap and post-swap disposition'
Assert-DeploymentTest -Condition (
    (Get-SswProgramRollbackDisposition -IsUpdate $true -InstallSwapped $false `
        -ProgramBackupTaken $false -InstallExists $true `
        -ProgramBackupExists $false) -eq 'AlreadyIntact'
) -Message 'An update failure before program swap must keep the existing program and allow service recovery.'
Assert-DeploymentTest -Condition (
    (Get-SswProgramRollbackDisposition -IsUpdate $true -InstallSwapped $true `
        -ProgramBackupTaken $true -InstallExists $true `
        -ProgramBackupExists $true) -eq 'RestoreBackup'
) -Message 'An update failure after program swap must restore the verified program backup.'
Assert-DeploymentTest -Condition (
    (Get-SswProgramRollbackDisposition -IsUpdate $false -InstallSwapped $true `
        -ProgramBackupTaken $false -InstallExists $true `
        -ProgramBackupExists $false) -eq 'QuarantineNewInstall'
) -Message 'A failed first install must quarantine the uncommitted program.'
$missingPreSwapProgramFailure = $null
try {
    Get-SswProgramRollbackDisposition -IsUpdate $true -InstallSwapped $false `
        -ProgramBackupTaken $false -InstallExists $false `
        -ProgramBackupExists $false | Out-Null
}
catch { $missingPreSwapProgramFailure = $_.Exception.Message }
Assert-DeploymentTest -Condition (
    $missingPreSwapProgramFailure -match '교체 전 실패'
) -Message 'A missing previous program during pre-swap update rollback must fail closed.'
$unbackedSwappedUpdateFailure = $null
try {
    Get-SswProgramRollbackDisposition -IsUpdate $true -InstallSwapped $true `
        -ProgramBackupTaken $false -InstallExists $true `
        -ProgramBackupExists $false | Out-Null
}
catch { $unbackedSwappedUpdateFailure = $_.Exception.Message }
Assert-DeploymentTest -Condition (
    $unbackedSwappedUpdateFailure -match '백업이 없어'
) -Message 'A swapped update without a verified backup must fail closed.'
$ambiguousFreshInstallFailure = $null
try {
    Get-SswProgramRollbackDisposition -IsUpdate $false -InstallSwapped $false `
        -ProgramBackupTaken $false -InstallExists $true `
        -ProgramBackupExists $false | Out-Null
}
catch { $ambiguousFreshInstallFailure = $_.Exception.Message }
Assert-DeploymentTest -Condition (
    $ambiguousFreshInstallFailure -match '완료 여부가 불명확'
) -Message 'A fresh install directory with no confirmed swap must be preserved and reported as recovery-required.'

Write-SswStep 'Best-effort dependency failure containment'
$dependencyState = [pscustomobject]@{
    ServiceQuiesced = $false
    DestructiveMutationRan = $false
    IndependentCleanupRan = $false
}
$dependencyErrors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'simulated-stop'; Action = {
        throw 'simulated stop failure'
    } },
    [pscustomobject]@{ Name = 'blocked-destructive-step'; Action = {
        if (-not $dependencyState.ServiceQuiesced) {
            throw 'dependency not satisfied'
        }
        $dependencyState.DestructiveMutationRan = $true
    } },
    [pscustomobject]@{ Name = 'independent-cleanup'; Action = {
        $dependencyState.IndependentCleanupRan = $true
    } }
))
Assert-DeploymentTest -Condition (
    $dependencyErrors -contains 'SIMULATED_STOP_FAILED' -and
    $dependencyErrors -contains 'BLOCKED_DESTRUCTIVE_STEP_FAILED' -and
    -not $dependencyState.DestructiveMutationRan -and
    $dependencyState.IndependentCleanupRan
) -Message 'Best-effort plans must preserve independent cleanup while dependency gates block destructive follow-up.'

Write-SswStep 'Simple UAC launcher and package contract'
Assert-ContainsAll -Name 'UAC launcher' -Text $launcher -Needles @(
    'install-agent.ps1',
    'Start-Process',
    '-Verb RunAs',
    '-Wait',
    'SSW_INSTALLER_PATH',
    '-EncodedCommand',
    'Read-Host',
    'Agent installation failed.',
    'Cause:',
    'AGENT_CONNECTION_REFUSED',
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
    'New-SswDirectoryIfMissing -Path $startMenuParent',
    'New-SswDirectoryIfMissing -Path $startupParent',
    'VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE',
    'if ($StartWithWindows) { Copy-Item -LiteralPath $startMenu -Destination $startup -Force }',
    'elseif ($DisableStartWithWindows -and (Test-Path -LiteralPath $startup -PathType Leaf))'
)

Write-SswStep 'Viewer shortcut directory helper behavior'
$shortcutDirectoryTestRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-shortcut-directory-{0}' -f [Guid]::NewGuid().ToString('N'))
$createdShortcutDirectory = Join-Path $shortcutDirectoryTestRoot 'Programs'
$occupiedShortcutDirectory = Join-Path $shortcutDirectoryTestRoot 'Not-A-Directory'
try {
    $created = New-SswDirectoryIfMissing -Path $createdShortcutDirectory `
        -FailureCode 'TEST_DIRECTORY_UNAVAILABLE' -Description '테스트 바로 가기'
    Assert-DeploymentTest -Condition $created -Message 'Missing shortcut directory was not reported as newly created.'
    Assert-DeploymentTest -Condition (Test-Path -LiteralPath $createdShortcutDirectory -PathType Container) `
        -Message 'Missing shortcut directory was not created.'

    $createdAgain = New-SswDirectoryIfMissing -Path $createdShortcutDirectory `
        -FailureCode 'TEST_DIRECTORY_UNAVAILABLE' -Description '테스트 바로 가기'
    Assert-DeploymentTest -Condition (-not $createdAgain) `
        -Message 'Existing shortcut directory was incorrectly reported as newly created.'

    $sentinel = Join-Path $createdShortcutDirectory 'keep.txt'
    [IO.File]::WriteAllText($sentinel, 'keep', (New-Object Text.UTF8Encoding($false)))
    Remove-SswEmptyDirectoryBestEffort -Path $createdShortcutDirectory
    Assert-DeploymentTest -Condition (Test-Path -LiteralPath $createdShortcutDirectory -PathType Container) `
        -Message 'Non-empty shortcut directory was removed.'
    Remove-Item -LiteralPath $sentinel -Force
    Remove-SswEmptyDirectoryBestEffort -Path $createdShortcutDirectory
    Assert-DeploymentTest -Condition (-not (Test-Path -LiteralPath $createdShortcutDirectory)) `
        -Message 'Empty installer-created shortcut directory was not removed.'

    New-Item -ItemType Directory -Path $shortcutDirectoryTestRoot -Force | Out-Null
    [IO.File]::WriteAllText($occupiedShortcutDirectory, 'file', (New-Object Text.UTF8Encoding($false)))
    $occupiedPathRejected = $false
    try {
        $null = New-SswDirectoryIfMissing -Path $occupiedShortcutDirectory `
            -FailureCode 'TEST_DIRECTORY_UNAVAILABLE' -Description '테스트 바로 가기'
    }
    catch {
        $occupiedPathRejected = $_.Exception.Message.StartsWith('TEST_DIRECTORY_UNAVAILABLE:')
    }
    Assert-DeploymentTest -Condition $occupiedPathRejected `
        -Message 'A file occupying the shortcut directory path was not rejected with a stable code.'
}
finally {
    if (Test-Path -LiteralPath $shortcutDirectoryTestRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $shortcutDirectoryTestRoot
        Remove-Item -LiteralPath $shortcutDirectoryTestRoot -Recurse -Force
    }
}

Assert-DeploymentTest -Condition (
    $mockSmoke.Contains("-ArgumentList '--service'") -and
    -not $mockSmoke.Contains("-ArgumentList '--background'")
) -Message 'Mock smoke test must exercise the service-only Agent runtime.'

Write-SswStep 'Viewer transactional rollback and commit boundary contract'
Assert-ContainsAll -Name 'Viewer transaction boundary' -Text $viewerInstall -Needles @(
    '$shortcutBackupsReady = $false',
    '$shortcutMutationStarted = $false',
    '$rollbackState = [pscustomobject]@{ ShortcutRestored = $false }',
    '$transactionCommitted = $false',
    '$shortcutBackupsReady = $true',
    '$shortcutMutationStarted = $true',
    '$rollbackState.ShortcutRestored = $true',
    'if (-not $rollbackState.ShortcutRestored)',
    'if (-not $shortcutBackupsReady) { throw',
    'Remove-SswEmptyDirectoryBestEffort -Path $startupParent',
    'Remove-SswEmptyDirectoryBestEffort -Path $startMenuParent',
    '$smokeProcess.HasExited -and $smokeProcess.ExitCode -ne 0',
    'Cause: $failureCode',
    'Recovery: $recovery',
    'Diagnostic: %LOCALAPPDATA%\SamsungSwitchWatch-Operations\viewer-install.json',
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

Write-SswStep 'Install and uninstall operations share fail-closed deployment locks'
Assert-ContainsAll -Name 'Deployment lock helper' -Text $common -Needles @(
    'function Get-SswDeploymentMutexName',
    'function New-SswDeploymentMutexSecurity',
    'function Enter-SswNamedDeploymentLock',
    'function Enter-SswDeploymentLock',
    'function Exit-SswDeploymentLock',
    'Global\SamsungSwitchWatch.Agent.Deployment.v1',
    'Global\SamsungSwitchWatch.Viewer.Deployment.',
    'DEPLOYMENT_ALREADY_RUNNING',
    'DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED',
    '$acquired = $mutex.WaitOne(0)',
    'S-1-5-18',
    'S-1-5-32-544'
)
Assert-ContainsAll -Name 'Agent durable deployment journal guard' -Text $common -Needles @(
    'function Test-SswTrustedAdministrativeOwnerSid',
    '$currentIdentity.User.Value -eq $normalizedSid',
    'WindowsBuiltInRole]::Administrator',
    'function Initialize-SswAgentOperationsRoot',
    'function Read-SswAgentDeploymentJournal',
    'function Assert-SswAgentDeploymentJournalsReady',
    'agent-install-or-update.json',
    'agent-uninstall.json',
    'AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID',
    'AGENT_DEPLOYMENT_JOURNAL_INVALID',
    'AGENT_DEPLOYMENT_RECOVERY_REQUIRED',
    '$journalItem.Length -gt 65536',
    '[IO.FileShare]::Read',
    'Set-SswInstallerBackupAcl -Path $root -ValidateExistingOwner'
)
Assert-DeploymentTest -Condition (
    -not $common.Contains('System.DirectoryServices.AccountManagement') -and
    -not $common.Contains('.GetMembers(')
) -Message 'Operations owner migration must not perform unbounded local or domain directory expansion.'
$agentMutexSecurity = New-SswDeploymentMutexSecurity -Product 'Agent'
$agentMutexRules = @($agentMutexSecurity.GetAccessRules(
    $true,
    $false,
    [Security.Principal.SecurityIdentifier]))
$agentMutexSids = @($agentMutexRules | ForEach-Object {
        $_.IdentityReference.Value
    } | Sort-Object -Unique)
Assert-DeploymentTest -Condition (
    ($agentMutexSids -join ',') -eq 'S-1-5-18,S-1-5-32-544'
) -Message 'Agent deployment mutex ACL must allow only SYSTEM and built-in Administrators.'
Assert-DeploymentTest -Condition $agentMutexSecurity.AreAccessRulesProtected `
    -Message 'Agent deployment mutex ACL inheritance must be disabled.'
foreach ($rule in $agentMutexRules) {
    Assert-DeploymentTest -Condition (
        $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $rule.MutexRights -eq [Security.AccessControl.MutexRights]::FullControl -and
        -not $rule.IsInherited
    ) -Message 'Every Agent deployment mutex ACL rule must be explicit Allow FullControl.'
}

$viewerMutexSecurity = New-SswDeploymentMutexSecurity -Product 'Viewer'
$viewerMutexRules = @($viewerMutexSecurity.GetAccessRules(
    $true,
    $false,
    [Security.Principal.SecurityIdentifier]))
$viewerMutexSids = @($viewerMutexRules | ForEach-Object {
        $_.IdentityReference.Value
    } | Sort-Object -Unique)
$expectedViewerMutexSids = @('S-1-5-18', (Get-SswCurrentUserSid)) | Sort-Object -Unique
Assert-DeploymentTest -Condition (
    ($viewerMutexSids -join ',') -eq ($expectedViewerMutexSids -join ',')
) -Message 'Viewer deployment mutex ACL must allow only SYSTEM and the current user SID.'
Assert-DeploymentTest -Condition $viewerMutexSecurity.AreAccessRulesProtected `
    -Message 'Viewer deployment mutex ACL inheritance must be disabled.'
foreach ($rule in $viewerMutexRules) {
    Assert-DeploymentTest -Condition (
        $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $rule.MutexRights -eq [Security.AccessControl.MutexRights]::FullControl -and
        -not $rule.IsInherited
    ) -Message 'Every Viewer deployment mutex ACL rule must be explicit Allow FullControl.'
}
$mismatchedProductFailure = $null
try {
    $null = Enter-SswNamedDeploymentLock `
        -Name (Get-SswDeploymentMutexName -Product 'Agent') -Product 'Viewer'
}
catch {
    $mismatchedProductFailure = $_.Exception.Message
}
Assert-DeploymentTest -Condition (
    [string]$mismatchedProductFailure -like 'DEPLOYMENT_LOCK_INVALID:*'
) -Message 'A production deployment lock name must not be opened with another product ACL.'
$deploymentScripts = @(
    [pscustomobject]@{
        Name = 'Agent installer'
        Text = $install
        Acquire = "Enter-SswDeploymentLock -Product 'Agent'"
    },
    [pscustomobject]@{
        Name = 'Agent uninstaller'
        Text = $uninstall
        Acquire = "Enter-SswDeploymentLock -Product 'Agent'"
    },
    [pscustomobject]@{
        Name = 'Viewer installer'
        Text = $viewerInstall
        Acquire = "Enter-SswDeploymentLock -Product 'Viewer'"
    },
    [pscustomobject]@{
        Name = 'Viewer uninstaller'
        Text = $viewerUninstall
        Acquire = "Enter-SswDeploymentLock -Product 'Viewer'"
    }
)
foreach ($deploymentScript in $deploymentScripts) {
    $acquireIndex = $deploymentScript.Text.IndexOf($deploymentScript.Acquire)
    $journalIndex = $deploymentScript.Text.IndexOf('Write-SswOperationJournal')
    $releaseIndex = $deploymentScript.Text.LastIndexOf(
        'Exit-SswDeploymentLock -Lock $deploymentLock')
    Assert-DeploymentTest -Condition (
        $acquireIndex -ge 0 -and
        $journalIndex -gt $acquireIndex -and
        $releaseIndex -gt $journalIndex
    ) -Message "$($deploymentScript.Name) must hold one product lock across every journaled mutation."
}

Write-SswStep 'Agent deployment journal producer ordering contract'
$agentTransactionStartIndex = $install.IndexOf(
    '$transactionId = [Guid]::NewGuid().ToString(''N'')')
$agentPreparedIndex = $install.IndexOf(
    "-Stage 'prepared' -Status 'running'",
    $agentTransactionStartIndex)
$agentTransactionCatchMatch = ([regex]'(?m)^catch \{').Match(
    $install,
    $agentPreparedIndex)
$agentTransactionCatchIndex = if ($agentTransactionCatchMatch.Success) {
    $agentTransactionCatchMatch.Index
}
else { -1 }
$agentMutationNeedles = @(
    'New-Item -ItemType Directory -Path $installParent, $staging, $transactionRoot',
    'Stop-ScheduledTask -TaskName $legacyBackgroundTaskName',
    'Stop-Service -Name $serviceName -Force',
    'Copy-Item -LiteralPath $data -Destination $dataSnapshot',
    'Move-Item -LiteralPath $install -Destination $programBackup',
    'Move-Item -LiteralPath $staging -Destination $install',
    '& sc.exe create $serviceName',
    '& sc.exe config $serviceName',
    "Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http'",
    'New-SswAgentHttpsFirewallRule -RemoteAddress $clientCidrs',
    'Start-Service -Name $serviceName',
    '[IO.File]::WriteAllText($temporaryReceipt'
)
Assert-DeploymentTest -Condition (
    $agentTransactionStartIndex -ge 0 -and
    $agentPreparedIndex -gt $agentTransactionStartIndex -and
    $agentTransactionCatchIndex -gt $agentPreparedIndex
) -Message 'Agent install prepared journal marker was not found in the production transaction block.'
foreach ($mutationNeedle in $agentMutationNeedles) {
    $mutationIndex = $install.IndexOf($mutationNeedle, $agentTransactionStartIndex)
    Assert-DeploymentTest -Condition (
        $mutationIndex -gt $agentPreparedIndex -and
        $mutationIndex -lt $agentTransactionCatchIndex
    ) -Message "Agent install mutation must follow the prepared journal: $mutationNeedle"
}

$agentCompletedIndex = $install.IndexOf(
    "-Stage 'completed' -Status 'succeeded'",
    $agentPreparedIndex)
$agentCommittedIndex = $install.IndexOf(
    '$transactionCommitted = $true',
    $agentPreparedIndex)
$agentCleanupIndex = $install.IndexOf(
    'foreach ($obsolete in @($programBackup, $transactionRoot))',
    $agentPreparedIndex)
Assert-DeploymentTest -Condition (
    $agentCompletedIndex -gt $agentPreparedIndex -and
    $agentCommittedIndex -gt $agentCompletedIndex -and
    $agentCleanupIndex -gt $agentCommittedIndex -and
    $agentCleanupIndex -lt $agentTransactionCatchIndex
) -Message 'Agent install must commit its journal before setting the commit flag and cleaning program/transaction backups.'

$agentUninstallTransactionStartIndex = $uninstall.IndexOf(
    '$transactionId = [Guid]::NewGuid().ToString(''N'')')
$agentUninstallPreparedIndex = $uninstall.IndexOf(
    "-Stage 'prepared' -Status 'running'",
    $agentUninstallTransactionStartIndex)
$agentUninstallPlanIndex = $uninstall.IndexOf(
    '$errors = @(Invoke-SswBestEffortPlan -Plan @(',
    $agentUninstallPreparedIndex)
$agentUninstallCompletedIndex = $uninstall.IndexOf(
    "-Stage 'completed' -Status `$status",
    $agentUninstallPlanIndex)
Assert-DeploymentTest -Condition (
    $agentUninstallTransactionStartIndex -ge 0 -and
    $agentUninstallPreparedIndex -gt $agentUninstallTransactionStartIndex -and
    $agentUninstallPlanIndex -gt $agentUninstallPreparedIndex -and
    $agentUninstallCompletedIndex -gt $agentUninstallPlanIndex
) -Message 'Agent uninstall must write prepared before its destructive plan and completed only after that plan.'
$agentUninstallMutationNeedles = @(
    'Stop-Service -Name $serviceName -Force',
    '& sc.exe delete $serviceName',
    "Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http'",
    'Remove-Item -LiteralPath $install -Recurse -Force',
    'Remove-Item -LiteralPath $data -Recurse -Force'
)
foreach ($mutationNeedle in $agentUninstallMutationNeedles) {
    $mutationIndex = $uninstall.IndexOf($mutationNeedle, $agentUninstallPlanIndex)
    Assert-DeploymentTest -Condition (
        $mutationIndex -gt $agentUninstallPlanIndex -and
        $mutationIndex -lt $agentUninstallCompletedIndex
    ) -Message "Agent uninstall mutation must remain inside the prepared/completed journal boundary: $mutationNeedle"
}

$agentStateReadIndex = $install.IndexOf('$existingService = Get-Service')
$agentLockIndex = $install.IndexOf("Enter-SswDeploymentLock -Product 'Agent'")
Assert-DeploymentTest -Condition (
    $agentStateReadIndex -gt $agentLockIndex
) -Message 'Agent installed state must be read only after the deployment lock is held.'
$agentJournalGuardIndex = $install.IndexOf(
    'Assert-SswAgentDeploymentJournalsReady -OperationsRoot $operationsRoot')
$agentJournalTrustIndex = $install.IndexOf(
    'Initialize-SswAgentOperationsRoot -OperationsRoot $operationsRoot')
Assert-DeploymentTest -Condition (
    $agentJournalTrustIndex -gt $agentLockIndex -and
    $agentJournalGuardIndex -gt $agentJournalTrustIndex -and
    $agentStateReadIndex -gt $agentJournalGuardIndex
) -Message 'Agent installer must secure the trust root and check both journals after locking and before reading mutable installed state.'
$agentUninstallLockIndex = $uninstall.IndexOf("Enter-SswDeploymentLock -Product 'Agent'")
$agentUninstallJournalGuardIndex = $uninstall.IndexOf(
    'Assert-SswAgentDeploymentJournalsReady -OperationsRoot $operationsRoot')
$agentUninstallJournalTrustIndex = $uninstall.IndexOf(
    'Initialize-SswAgentOperationsRoot -OperationsRoot $operationsRoot')
$agentUninstallStateReadIndex = $uninstall.IndexOf('$configPath = Join-Path $install')
Assert-DeploymentTest -Condition (
    $agentUninstallJournalTrustIndex -gt $agentUninstallLockIndex -and
    $agentUninstallJournalGuardIndex -gt $agentUninstallJournalTrustIndex -and
    $agentUninstallStateReadIndex -gt $agentUninstallJournalGuardIndex
) -Message 'Agent uninstaller must secure the trust root and check both journals after locking and before reading mutable installed state.'

$lockTestId = [Guid]::NewGuid().ToString('N')
$lockTestFolderName = [string]::Concat(
    'SamsungSwitchWatch ',
    [char]0xBC30,
    [char]0xD3EC,
    ' ',
    [char]0xC7A0,
    [char]0xAE08,
    ' ',
    $lockTestId)
$lockTestRoot = Join-Path ([IO.Path]::GetTempPath()) $lockTestFolderName
Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $lockTestRoot
$lockReadyPath = Join-Path $lockTestRoot 'child-ready.txt'
$lockName = 'Global\SamsungSwitchWatch.Deployment.Test.{0}' -f $lockTestId
$childProcess = $null
$witnessMutex = $null
$parentLock = $null
$childCommand = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. $env:SSW_DEPLOYMENT_LOCK_COMMON
$lock = $null
try {
    $lock = Enter-SswNamedDeploymentLock -Name $env:SSW_DEPLOYMENT_LOCK_NAME -Product 'Test'
    [IO.File]::WriteAllText($env:SSW_DEPLOYMENT_LOCK_READY, 'ready')
    Start-Sleep -Seconds 60
}
finally {
    Exit-SswDeploymentLock -Lock $lock
}
'@
try {
    New-Item -ItemType Directory -Path $lockTestRoot | Out-Null
    $encodedChildCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($childCommand))
    $childStart = New-Object Diagnostics.ProcessStartInfo
    $childStart.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $childStart.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedChildCommand"
    $childStart.UseShellExecute = $false
    $childStart.CreateNoWindow = $true
    $childStart.EnvironmentVariables['SSW_DEPLOYMENT_LOCK_COMMON'] = (
        Join-Path $PSScriptRoot 'common.ps1')
    $childStart.EnvironmentVariables['SSW_DEPLOYMENT_LOCK_NAME'] = $lockName
    $childStart.EnvironmentVariables['SSW_DEPLOYMENT_LOCK_READY'] = $lockReadyPath
    $childProcess = [Diagnostics.Process]::Start($childStart)

    for ($attempt = 0; $attempt -lt 100 -and
        -not (Test-Path -LiteralPath $lockReadyPath -PathType Leaf); $attempt++) {
        if ($childProcess.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    Assert-DeploymentTest -Condition (
        -not $childProcess.HasExited -and
        (Test-Path -LiteralPath $lockReadyPath -PathType Leaf)
    ) -Message 'The child Windows PowerShell process must hold the deployment lock.'

    $busyFailure = $null
    try {
        $parentLock = Enter-SswNamedDeploymentLock -Name $lockName -Product 'Test'
    }
    catch {
        $busyFailure = $_.Exception.Message
    }
    finally {
        if ($parentLock) {
            Exit-SswDeploymentLock -Lock $parentLock
            $parentLock = $null
        }
    }
    Assert-DeploymentTest -Condition (
        [string]$busyFailure -like 'DEPLOYMENT_ALREADY_RUNNING:*'
    ) -Message 'A second deployment process must fail closed without waiting.'

    # Keep one witness handle alive so the killed owner leaves an observable abandoned mutex.
    $witnessMutex = [Threading.Mutex]::OpenExisting($lockName)
    $childProcess.Kill()
    Assert-DeploymentTest -Condition ($childProcess.WaitForExit(5000)) `
        -Message 'The lock-holder child process must terminate within five seconds.'

    $interruptedFailure = $null
    try {
        $parentLock = Enter-SswNamedDeploymentLock -Name $lockName -Product 'Test'
    }
    catch {
        $interruptedFailure = $_.Exception.Message
    }
    finally {
        if ($parentLock) {
            Exit-SswDeploymentLock -Lock $parentLock
            $parentLock = $null
        }
    }
    Assert-DeploymentTest -Condition (
        [string]$interruptedFailure -like 'DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED:*'
    ) -Message 'An abandoned deployment lock must stop automatic changes with a stable code.'

    $parentLock = Enter-SswNamedDeploymentLock -Name $lockName -Product 'Test'
    Assert-DeploymentTest -Condition ($null -ne $parentLock) `
        -Message 'The deployment lock must be reusable after the abandoned state is acknowledged.'
    Exit-SswDeploymentLock -Lock $parentLock
    $parentLock = $null

    $parentLock = Enter-SswNamedDeploymentLock -Name $lockName -Product 'Test'
    Assert-DeploymentTest -Condition ($null -ne $parentLock) `
        -Message 'A normally released deployment lock must be immediately reusable.'
    Exit-SswDeploymentLock -Lock $parentLock
    $parentLock = $null
}
finally {
    if ($parentLock) { Exit-SswDeploymentLock -Lock $parentLock }
    if ($childProcess) {
        if (-not $childProcess.HasExited) {
            $childProcess.Kill()
            $childProcess.WaitForExit(5000) | Out-Null
        }
        $childProcess.Dispose()
    }
    if ($witnessMutex) { $witnessMutex.Dispose() }
    if (Test-Path -LiteralPath $lockTestRoot -PathType Container) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $lockTestRoot
        Remove-Item -LiteralPath $lockTestRoot -Recurse -Force
    }
}
Assert-DeploymentTest -Condition (-not (Test-Path -LiteralPath $lockTestRoot)) `
    -Message 'Deployment lock test files must be removed.'
$residualMutex = $null
try {
    $residualMutex = [Threading.Mutex]::OpenExisting($lockName)
}
catch [Threading.WaitHandleCannotBeOpenedException] {
}
finally {
    if ($residualMutex) { $residualMutex.Dispose() }
}
Assert-DeploymentTest -Condition ($null -eq $residualMutex) `
    -Message 'Every deployment lock test handle must be disposed.'

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
