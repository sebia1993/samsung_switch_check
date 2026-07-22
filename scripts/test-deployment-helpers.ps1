param()

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-DeploymentTest {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw "DEPLOYMENT_TEST_FAILED: $Message" }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("SamsungSwitchWatch-DeploymentTest-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    Write-SswStep 'fault injection: 복구 계획이 첫 오류 뒤에도 계속 실행되는지 확인'
    $continued = $false
    $faultErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'injected-fault'; Action = { throw 'expected injected failure' } },
        [pscustomobject]@{ Name = 'must-continue'; Action = { $script:continued = $true } }
    ))
    Assert-DeploymentTest -Condition ($faultErrors.Count -eq 1 -and $faultErrors[0] -eq 'INJECTED_FAULT_FAILED') `
        -Message 'fault injection 오류 코드가 안정적이지 않습니다.'
    Assert-DeploymentTest -Condition $continued -Message '복구 계획이 첫 실패에서 중단되었습니다.'

    Write-SswStep '작업 저널 원자적 갱신 계약 확인'
    $journal = Join-Path $temporaryRoot 'journal.json'
    Write-SswOperationJournal -Path $journal -Operation 'test' -TransactionId 'tx-test' -Stage 'one' -Status 'running'
    Write-SswOperationJournal -Path $journal -Operation 'test' -TransactionId 'tx-test' -Stage 'two' -Status 'failed' `
        -ErrorCodes @('INJECTED_FAULT_FAILED')
    $journalObject = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-DeploymentTest -Condition ($journalObject.stage -eq 'two' -and $journalObject.errorCodes[0] -eq 'INJECTED_FAULT_FAILED') `
        -Message '작업 저널 최종 상태가 올바르지 않습니다.'
    Assert-DeploymentTest -Condition (@(Get-ChildItem -LiteralPath $temporaryRoot -Filter '*.tmp').Count -eq 0) `
        -Message '원자적 저널 갱신 후 임시 파일이 남았습니다.'

    $agentInstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-agent.ps1') -Raw -Encoding UTF8
    $viewerInstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-viewer.ps1') -Raw -Encoding UTF8
    $agentUninstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'uninstall-agent.ps1') -Raw -Encoding UTF8
    $viewerAccess = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'set-viewer-access.ps1') -Raw -Encoding UTF8
    $credentialUpdate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'set-switch-credential.ps1') -Raw -Encoding UTF8
    $backgroundInstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-agent-background.ps1') -Raw -Encoding UTF8
    $backgroundRunner = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'run-agent-background.ps1') -Raw -Encoding UTF8
    $backgroundUninstall = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'uninstall-agent-background.ps1') -Raw -Encoding UTF8

    Write-SswStep 'Agent 패키지·데이터·방화벽·롤백 계약 확인'
    $commonText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8
    Assert-DeploymentTest -Condition ($commonText.Contains('$rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint')) `
        -Message 'ACL helper가 대상 루트 junction/symlink를 거부하지 않습니다.'
    foreach ($requiredText in @(
        '[switch]$ReuseData', 'install-receipt.json', 'switchwatch.db-wal', 'switchwatch.db-shm',
        'restore-database', 'Restore-SswAgentFirewallSnapshot', 'Test-SswOwnedAgentFirewallRule',
        'BUILD-MANIFEST.json', 'Get-FileHash', '[int]$HttpPort = 18443',
        '[ValidateCount(1, 32)][string[]]$ViewerRemoteAddress', 'http://0.0.0.0:$HttpPort',
        'viewerRemoteAddresses', 'restore-service-environment', 'legacyOwnedCertificateThumbprint',
        '[string]$SwitchesJsonPath', "[ValidateSet('IES4224GP', 'IES4028XP', 'IES4226XP')]", 'switchInventoryHash'
    )) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($requiredText) -Message "Agent 설치 계약 누락: $requiredText"
    }
    Assert-DeploymentTest -Condition ($agentInstall.IndexOf("Name = 'restore-database'") -lt $agentInstall.IndexOf("Name = 'restore-program'")) `
        -Message 'Agent rollback에서 DB가 프로그램보다 먼저 복원되지 않습니다.'
    Assert-DeploymentTest -Condition ($agentInstall.IndexOf('Invoke-SswLocalHealthProbe') -lt $agentInstall.IndexOf("Stage 'completed'")) `
        -Message 'readiness 확인 전에 설치 성공을 기록합니다.'
    Assert-DeploymentTest -Condition ($agentInstall.Contains('$shouldStart = $Repair -or') -and
        $agentInstall.Contains('$previousServiceWasRunning -and -not $DoNotStart') -and
        $agentInstall.Contains('readiness (clean environment)')) `
        -Message 'Repair가 서비스 시작 상태와 무관하게 readiness/schema를 검증한 뒤 이전 상태를 복원하지 않습니다.'

    Write-SswStep '옵트인 읽기 전용 장비 명령 설치 계약 확인'
    foreach ($queryInstallText in @(
        '[switch]$EnableReadOnlyQueries',
        "`$enableReadOnlyQueriesWasSpecified = `$PSBoundParameters.ContainsKey('EnableReadOnlyQueries')",
        'EnableReadOnlyQueries = [bool]$EnableReadOnlyQueries',
        'ReadOnlyQueryMaxCommandLength = 128',
        'ReadOnlyQueryMaxOutputBytes = 65536',
        'ReadOnlyQueryRateLimitPerMinute = 12',
        'ReadOnlyQueryDeviceWaitSeconds = 5',
        'ReadOnlyQueryTotalTimeoutSeconds = 60',
        "elseif (-not `$migrated.Agent.PSObject.Properties['EnableReadOnlyQueries'])"
    )) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($queryInstallText) `
            -Message "Agent 읽기 전용 장비 명령 설치 계약 누락: $queryInstallText"
    }
    foreach ($backgroundQueryText in @(
        '[switch]$EnableReadOnlyQueries',
        'ReadOnlyQueryMaxCommandLength = 128',
        'ReadOnlyQueryMaxOutputBytes = 65536',
        "elseif (-not `$result.Agent.PSObject.Properties['EnableReadOnlyQueries'])"
    )) {
        Assert-DeploymentTest -Condition $backgroundInstall.Contains($backgroundQueryText) `
            -Message "숨김 Agent 읽기 전용 장비 명령 설치 계약 누락: $backgroundQueryText"
    }
    Assert-DeploymentTest -Condition ($agentInstall.IndexOf('Invoke-SswLocalHealthProbe') -lt
        $agentInstall.IndexOf("Write-SswStep '검증 성공 후 구 인증서 서비스 secret 제거'")) `
        -Message 'Repair가 새 Agent readiness/schema 검증 전에 구 인증서 서비스 secret을 제거합니다.'
    $readinessIndex = $agentInstall.IndexOf('Invoke-SswLocalHealthProbe')
    $receiptIndex = $agentInstall.IndexOf('receiptVersion = 2')
    $legacyCertificateRemovalIndex = $agentInstall.IndexOf('Remove-Item -LiteralPath $legacyCertificatePath')
    Assert-DeploymentTest -Condition ($readinessIndex -ge 0 -and $receiptIndex -gt $readinessIndex -and
        $legacyCertificateRemovalIndex -gt $receiptIndex) `
        -Message 'v0.5 Repair가 readiness와 v2 receipt 검증 전에 구 설치기 소유 인증서를 제거할 수 있습니다.'
    foreach ($rollbackText in @('switchwatch.db-wal', 'switchwatch.db-shm', 'restore-service-environment',
        'restore-database', 'restore-program', 'restore-firewall', 'restart-previous-service')) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($rollbackText) -Message "v0.5 Repair rollback 계약 누락: $rollbackText"
    }
    foreach ($configurationMigrationText in @(
        '$ExistingConfig | ConvertTo-Json -Depth 20 | ConvertFrom-Json',
        "@('Https', 'PairingCodeLifetimeMinutes', 'TokenPepper', 'Tokens')",
        '-NotePropertyName ListenUrl', '-NotePropertyName DataDirectory', '-NotePropertyName Switches'
    )) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($configurationMigrationText) `
            -Message "기존 Agent 지원 설정 전체 보존/HTTP 전환 계약 누락: $configurationMigrationText"
    }
    foreach ($dataPreservationText in @('$installedDataDirectory = [IO.Path]::GetFullPath',
        "`$PSBoundParameters.ContainsKey('DataDirectory')", "`$data.Equals(`$installedDataDirectory",
        "`$data = `$installedDataDirectory", "`$receiptPath = Join-Path `$data 'install-receipt.json'")) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($dataPreservationText) `
            -Message "Repair custom DataDirectory 보존 계약 누락: $dataPreservationText"
    }
    Assert-DeploymentTest -Condition ($agentUninstall.Contains('Invoke-SswBestEffortPlan') -and $agentUninstall.Contains('Remove-SswOwnedAgentFirewallRule')) `
        -Message 'Agent 제거가 단계별 계속 실행/소유 방화벽 계약을 사용하지 않습니다.'
    Assert-DeploymentTest -Condition (-not $agentInstall.Contains('[switch]$SkipFirewall') -and
        -not $agentInstall.Contains('$HttpsPort') -and -not $agentInstall.Contains('[switch]$RotateCertificate')) `
        -Message '제거된 HTTPS/방화벽 우회 설치 옵션이 남아 있습니다.'

    Write-SswStep '비관리자 현재 사용자 숨김 Agent 계약 확인'
    foreach ($requiredText in @(
        '[string]$SourceDirectory = $PSScriptRoot', '[switch]$Repair', '[switch]$Preflight',
        'Programs\SamsungSwitchWatch\Agent', 'SamsungSwitchWatch\AgentData',
        'Get-SswAgentBackgroundTaskName', 'Get-SswCurrentUserSid',
        'New-ScheduledTaskTrigger -AtLogOn', "`$taskTrigger.Delay = 'PT15S'",
        'New-ScheduledTaskPrincipal -UserId $ownerSid -LogonType Interactive -RunLevel Limited',
        '-WindowStyle Hidden', '-MultipleInstances IgnoreNew', '-RestartCount 3',
        '-InstallDirectory `"$install`"',
        "Get-Service -Name (Get-SswAgentServiceName)", 'Test-SswTcpPortAvailable',
        'Invoke-SswLocalLivenessProbe', 'Register-ScheduledTask', 'rollback-completed',
        'restore-data', 'restore-program', 'restore-old-task', 'background-install-receipt.json',
        'Test-SswBackgroundTaskRegistrationMarker'
    )) {
        Assert-DeploymentTest -Condition $backgroundInstall.Contains($requiredText) `
            -Message "현재 사용자 숨김 Agent 설치 계약 누락: $requiredText"
    }
    Assert-DeploymentTest -Condition ($backgroundInstall.IndexOf("if (`$Preflight)") -lt
        $backgroundInstall.IndexOf('Write-SswOperationJournal')) `
        -Message '숨김 Agent Preflight가 작업 저널이나 설치 변경보다 먼저 종료되지 않습니다.'
    Assert-DeploymentTest -Condition ($backgroundInstall.IndexOf('Invoke-SswLocalLivenessProbe') -lt
        $backgroundInstall.IndexOf('Write-SswAtomicText -Path $receiptPath')) `
        -Message '숨김 Agent 설치가 /health/live 확인 전에 설치 영수증을 확정합니다.'
    foreach ($forbiddenPattern in @('Assert-SswAdministrator', 'New-NetFirewallRule', 'Set-NetFirewall',
        'Remove-NetFirewall', 'Get-Process\s+-Name\s+[''"]SamsungSwitchWatch\.Agent')) {
        Assert-DeploymentTest -Condition ($backgroundInstall -notmatch $forbiddenPattern -and
            $backgroundUninstall -notmatch $forbiddenPattern) `
            -Message "현재 사용자 숨김 모드에 관리자·방화벽·광범위 프로세스 변경이 포함되어 있습니다: $forbiddenPattern"
    }
    foreach ($runnerText in @('DOTNET_ENVIRONMENT', '& $exe', 'Global\SamsungSwitchWatchAgent-CurrentUser-',
        'if ($exitCode -eq 0) { $exitCode = 1 }', 'background-runner.log',
        'AGENT_START_FAILED', 'RUNNER_FAILED')) {
        Assert-DeploymentTest -Condition $backgroundRunner.Contains($runnerText) `
            -Message "숨김 Agent 실행기 수명주기 계약 누락: $runnerText"
    }
    Assert-DeploymentTest -Condition (-not $backgroundRunner.Contains('Start-Process')) `
        -Message '숨김 실행기가 Agent를 분리 실행해 예약 작업이 수명주기를 추적하지 못합니다.'
    foreach ($uninstallText in @('[switch]$RemoveData', 'Assert-SswBackgroundAgentReceipt',
        'Test-SswOwnedBackgroundTask', 'Stop-SswOwnedBackgroundProcesses', 'Unregister-ScheduledTask',
        'if ($RemoveData', '보존했습니다')) {
        Assert-DeploymentTest -Condition $backgroundUninstall.Contains($uninstallText) `
            -Message "현재 사용자 숨김 Agent 제거 계약 누락: $uninstallText"
    }
    Assert-DeploymentTest -Condition ($backgroundUninstall.IndexOf('if ($shutdownErrors.Count -gt 0)') -lt
        $backgroundUninstall.IndexOf('Unregister-ScheduledTask')) `
        -Message '숨김 Agent 제거가 중지 오류를 확인하기 전에 예약 작업을 제거할 수 있습니다.'
    foreach ($credentialBackgroundText in @('[switch]$CurrentUser', 'Assert-SswBackgroundAgentReceipt',
        'Get-SswAgentBackgroundTaskName', 'Stop-ScheduledTask', 'Invoke-SswLocalLivenessProbe')) {
        Assert-DeploymentTest -Condition $credentialUpdate.Contains($credentialBackgroundText) `
            -Message "현재 사용자 자격 증명 갱신 계약 누락: $credentialBackgroundText"
    }

    Write-SswStep '비파괴 예약 작업 정의 직렬화 확인'
    Import-Module ScheduledTasks -ErrorAction Stop
    $testIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $testInstall = Join-Path $env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Agent'
    $testRunner = Join-Path $testInstall 'run-agent-background.ps1'
    $testPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $testArguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$testRunner`" -InstallDirectory `"$testInstall`""
    $testAction = New-ScheduledTaskAction -Execute $testPowerShell -Argument $testArguments -WorkingDirectory $testInstall
    $testTrigger = New-ScheduledTaskTrigger -AtLogOn -User $testIdentity.Name
    $testTrigger.Delay = 'PT15S'
    $testPrincipal = New-ScheduledTaskPrincipal -UserId $testIdentity.User.Value -LogonType Interactive -RunLevel Limited
    $testSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -Hidden -DontStopOnIdleEnd -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1))
    $testTask = New-ScheduledTask -Action $testAction -Trigger $testTrigger -Principal $testPrincipal `
        -Settings $testSettings -Description 'Owned by SamsungSwitchWatch current-user background installer v1'
    $testTaskXml = Export-ScheduledTask -InputObject $testTask
    foreach ($xmlFragment in @('<LogonType>InteractiveToken</LogonType>', '<RunLevel>LeastPrivilege</RunLevel>',
        '<Delay>PT15S</Delay>', '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>',
        '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>', '<Interval>PT1M</Interval>', '<Count>3</Count>',
        '<Hidden>true</Hidden>', '<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>',
        '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>')) {
        Assert-DeploymentTest -Condition $testTaskXml.Contains($xmlFragment) `
            -Message "예약 작업 정의 XML 계약 누락: $xmlFragment"
    }

    Write-SswStep '복수 고정 IPv4 정규화 및 서브넷 거부 계약 확인'
    $normalized = @(ConvertTo-SswViewerRemoteAddresses -Address @('10.0.0.10', '10.0.0.2', '10.0.0.10'))
    Assert-DeploymentTest -Condition (($normalized -join ',') -eq '10.0.0.2,10.0.0.10') `
        -Message 'Viewer IPv4 주소의 중복 제거/숫자 정렬 결과가 올바르지 않습니다.'
    $subnetRejected = $false
    try { $null = ConvertTo-SswViewerRemoteAddresses -Address @('10.0.0.0/24') }
    catch { $subnetRejected = $true }
    Assert-DeploymentTest -Condition $subnetRejected -Message 'Viewer 방화벽 입력이 서브넷을 거부하지 않습니다.'

    foreach ($nonCanonicalAddress in @(
        '10.20.30',
        '10.1',
        '1',
        '0x0A000001',
        '010.020.030.040',
        '10.20.30.256'
    )) {
        $nonCanonicalRejected = $false
        try { $null = ConvertTo-SswViewerRemoteAddresses -Address @($nonCanonicalAddress) }
        catch { $nonCanonicalRejected = $true }
        Assert-DeploymentTest -Condition $nonCanonicalRejected `
            -Message "canonical dotted-quad가 아닌 Viewer 주소를 거부하지 않습니다: $nonCanonicalAddress"
    }

    Write-SswStep '방화벽 포트 중첩 및 정확한 프로필 집합 계약 확인'
    Assert-DeploymentTest -Condition (Test-SswFirewallPortOverlap -Protocol 'TCP' -LocalPort @('Any') -TargetPort 18443) `
        -Message 'TCP Any 포트 규칙과 Agent 포트의 중첩을 탐지하지 못했습니다.'
    Assert-DeploymentTest -Condition (Test-SswFirewallPortOverlap -Protocol '6' -LocalPort @('18000-19000') -TargetPort 18443) `
        -Message 'TCP 숫자 프로토콜의 포트 범위 중첩을 탐지하지 못했습니다.'
    Assert-DeploymentTest -Condition (Test-SswFirewallPortOverlap -Protocol 'Any' -LocalPort @('18443') -TargetPort 18443) `
        -Message 'Any 프로토콜의 Agent 포트 중첩을 탐지하지 못했습니다.'
    Assert-DeploymentTest -Condition (-not (Test-SswFirewallPortOverlap -Protocol 'UDP' -LocalPort @('18443') -TargetPort 18443)) `
        -Message 'UDP 전용 규칙을 Agent TCP 포트 중첩으로 잘못 판단했습니다.'
    Assert-DeploymentTest -Condition (-not (Test-SswFirewallPortOverlap -Protocol 'TCP' -LocalPort @('18444') -TargetPort 18443)) `
        -Message '다른 TCP 포트를 Agent 포트 중첩으로 잘못 판단했습니다.'

    Assert-DeploymentTest -Condition (Test-SswFirewallProfileSetExact -Profile 'Domain, Private') `
        -Message '제품 방화벽 규칙의 정확한 Domain+Private 프로필을 허용하지 않습니다.'
    foreach ($invalidProfileSet in @('Any', 'All', 'Domain', 'Private', 'Public', 'Domain,Private,Public')) {
        Assert-DeploymentTest -Condition (-not (Test-SswFirewallProfileSetExact -Profile $invalidProfileSet)) `
            -Message "제품 방화벽 규칙이 정확하지 않은 프로필 집합을 허용합니다: $invalidProfileSet"
    }
    $exactFirewallSnapshot = [pscustomobject]@{
        Name = 'SamsungSwitchWatchAgent-Http'; DisplayName = 'Samsung Switch Watch Agent HTTP'
        Group = 'Samsung Switch Watch'; Description = 'Owned by SamsungSwitchWatchAgent installer v2'
        Enabled = 'True'; Direction = 'Inbound'; Action = 'Allow'; Protocol = 'TCP'; LocalPort = '18443'
        RemotePort = 'Any'; LocalAddress = @('Any'); RemoteAddress = @('10.0.0.2')
        Program = 'Any'; Service = 'Any'; InterfaceType = 'Any'; Profile = 'Domain, Private'
    }
    Assert-DeploymentTest -Condition (Test-SswAgentFirewallRuleExact -Snapshot $exactFirewallSnapshot -Port 18443 `
        -RemoteAddress @('10.0.0.2')) -Message '완전한 제품 방화벽 규칙을 exact로 인정하지 않습니다.'
    foreach ($restrictedFilter in @(
        @{ Name = 'RemotePort'; Value = '443' }, @{ Name = 'LocalAddress'; Value = @('127.0.0.1') },
        @{ Name = 'Program'; Value = 'C:\foreign.exe' }, @{ Name = 'Service'; Value = 'foreign-service' },
        @{ Name = 'InterfaceType'; Value = 'Wireless' }
    )) {
        $restrictedSnapshot = $exactFirewallSnapshot | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $restrictedSnapshot.($restrictedFilter.Name) = $restrictedFilter.Value
        Assert-DeploymentTest -Condition (-not (Test-SswAgentFirewallRuleExact -Snapshot $restrictedSnapshot `
            -Port 18443 -RemoteAddress @('10.0.0.2'))) `
            -Message "가용성을 제한하는 방화벽 filter를 exact로 잘못 인정합니다: $($restrictedFilter.Name)"
    }

    Write-SswStep '설치 영수증 제품·버전·Agent·인벤토리 결속 계약 확인'
    $receiptAgentId = 'agent-test'
    $receiptInventoryHash = ('a' * 64)
    foreach ($supportedReceiptVersion in @(1, 2)) {
        $validReceipt = [pscustomobject]@{
            product = 'SamsungSwitchWatchAgent'
            receiptVersion = $supportedReceiptVersion
            agentId = $receiptAgentId
            switchCount = 2
            switchInventoryHash = $receiptInventoryHash
        }
        $validatedReceiptVersion = Assert-SswAgentInstallReceipt -Receipt $validReceipt -AgentId $receiptAgentId `
            -SwitchInventoryHash $receiptInventoryHash -SwitchCount 2
        Assert-DeploymentTest -Condition ($validatedReceiptVersion -eq $supportedReceiptVersion) `
            -Message "지원 영수증 버전을 검증하지 못했습니다: v$supportedReceiptVersion"
    }
    foreach ($receiptMutation in @(
        @{ Name = 'product'; Value = 'ForeignAgent' },
        @{ Name = 'receiptVersion'; Value = 3 },
        @{ Name = 'agentId'; Value = 'other-agent' },
        @{ Name = 'switchCount'; Value = 1 },
        @{ Name = 'switchInventoryHash'; Value = ('b' * 64) }
    )) {
        $invalidReceipt = [pscustomobject]@{
            product = 'SamsungSwitchWatchAgent'
            receiptVersion = 2
            agentId = $receiptAgentId
            switchCount = 2
            switchInventoryHash = $receiptInventoryHash
        }
        $invalidReceipt.($receiptMutation.Name) = $receiptMutation.Value
        $invalidReceiptRejected = $false
        try {
            $null = Assert-SswAgentInstallReceipt -Receipt $invalidReceipt -AgentId $receiptAgentId `
                -SwitchInventoryHash $receiptInventoryHash -SwitchCount 2
        }
        catch { $invalidReceiptRejected = $true }
        Assert-DeploymentTest -Condition $invalidReceiptRejected `
            -Message "변조된 설치 영수증을 거부하지 않습니다: $($receiptMutation.Name)"
    }

    Write-SswStep '방화벽 단독 게이트와 설치·갱신·제거 안전 호출 계약 확인'
    foreach ($gatePattern in @(
        "Get-Service\s+-Name\s+'MpsSvc'",
        "\.Status\s+-ne\s+'Running'",
        'Get-NetFirewallProfile\s+-Name\s+Domain,Private,Public',
        "@\('Domain',\s*'Private',\s*'Public'\)",
        "DefaultInboundAction\s+-eq\s+'Allow'",
        "AllowInboundRules\s+-eq\s+'False'",
        "AllowLocalFirewallRules\s+-eq\s+'False'",
        'Get-NetFirewallRule\s+-Enabled\s+True\s+-Direction\s+Inbound\s+-Action\s+Allow',
        'Test-SswFirewallPortOverlap',
        'Test-SswFirewallRuleMayApplyToAgent',
        '제품 소유가 아닌 활성 인바운드 Allow 규칙'
    )) {
        Assert-DeploymentTest -Condition ($commonText -match $gatePattern) `
            -Message "방화벽 단독 게이트 계약 누락: $gatePattern"
    }
    Assert-DeploymentTest -Condition ($commonText -notmatch '\$profileText\s*=') `
        -Message '외부 인바운드 Allow 중첩 검사가 특정 프로필 규칙만 선별해 다른 프로필 규칙을 놓칠 수 있습니다.'
    Assert-DeploymentTest -Condition ($agentInstall -match 'Assert-SswAgentFirewallGateReady\s+-Port\s+\$HttpPort') `
        -Message 'Agent 설치/Repair가 방화벽 단독 게이트 사전 검사를 호출하지 않습니다.'
    Assert-DeploymentTest -Condition ($viewerAccess -match 'Assert-SswAgentFirewallGateReady\s+-Port\s+\$httpPort') `
        -Message 'Viewer 허용 주소 갱신이 방화벽 단독 게이트 사전 검사를 호출하지 않습니다.'
    Assert-DeploymentTest -Condition ($credentialUpdate -match 'Assert-SswCredentialStartFirewall' -and
        $credentialUpdate -match 'Assert-SswAgentInstallReceipt' -and
        $credentialUpdate -match 'Test-SswAgentFirewallRuleExact') `
        -Message '자격 증명 저장/최초 시작이 v2 receipt와 exact 방화벽 규칙을 재검증하지 않습니다.'
    Assert-DeploymentTest -Condition ($agentUninstall -match 'Assert-SswAgentFirewallNameSafety') `
        -Message 'Agent 제거가 제품 내부 방화벽 이름 충돌을 변경 전에 검사하지 않습니다.'

    foreach ($receiptConsumer in @(
        [pscustomobject]@{ Name = 'install-agent'; Text = $agentInstall },
        [pscustomobject]@{ Name = 'set-viewer-access'; Text = $viewerAccess }
    )) {
        Assert-DeploymentTest -Condition ($receiptConsumer.Text -match 'Assert-SswAgentInstallReceipt') `
            -Message "$($receiptConsumer.Name)가 Agent ID/인벤토리 결속 영수증 검증을 사용하지 않습니다."
    }

    foreach ($scriptContract in @(
        [pscustomobject]@{ Name = 'set-viewer-access'; Text = $viewerAccess },
        [pscustomobject]@{ Name = 'uninstall-agent'; Text = $agentUninstall }
    )) {
        foreach ($dataPattern in @(
            '\$installedDataDirectory\s*=\s*\[IO\.Path\]::GetFullPath',
            '\$PSBoundParameters\.ContainsKey\(''DataDirectory''\)',
            '\$data\.Equals\(\$installedDataDirectory',
            '\$data\s*=\s*\$installedDataDirectory',
            '\$receiptPath\s*=\s*Join-Path\s+\$data\s+''install-receipt\.json'''
        )) {
            Assert-DeploymentTest -Condition ($scriptContract.Text -match $dataPattern) `
                -Message "$($scriptContract.Name)의 custom DataDirectory 채택/불일치 거부 계약 누락: $dataPattern"
        }
    }

    foreach ($certificateHelperPattern in @(
        'function\s+Get-SswLegacyOwnedAgentCertificateThumbprints',
        'certificateOwnedByInstaller',
        'certificateStoreThumbprint',
        'previousCertificateOwnedByInstaller',
        'previousCertificateStoreThumbprint',
        '\$certificate\.FriendlyName\s+-eq\s+\$expectedFriendlyName',
        'Get-SswCertificateSha256',
        'Cert:\\LocalMachine\\My\\'
    )) {
        Assert-DeploymentTest -Condition ($commonText -match $certificateHelperPattern) `
            -Message "v0.5 active/previous 인증서 소유권 검증 helper 계약 누락: $certificateHelperPattern"
    }
    foreach ($certificateConsumer in @(
        [pscustomobject]@{ Name = 'Repair'; Text = $agentInstall },
        [pscustomobject]@{ Name = 'Uninstall'; Text = $agentUninstall }
    )) {
        foreach ($certificateConsumerPattern in @(
            'Get-SswLegacyOwnedAgentCertificateThumbprints',
            'Remove-Item\s+-LiteralPath\s+\$(?:legacy)?CertificatePath'
        )) {
            Assert-DeploymentTest -Condition ($certificateConsumer.Text -match $certificateConsumerPattern) `
                -Message "$($certificateConsumer.Name)의 v0.5 active/previous 인증서 정리 계약 누락: $certificateConsumerPattern"
        }
    }

    foreach ($requiredText in @('Set-NetFirewallAddressFilter -RemoteAddress', '[IO.File]::Replace($temporaryReceipt',
        'Test-SswAgentFirewallRuleExact', 'Restore-SswAgentFirewallSnapshot', 'Invoke-SswBestEffortPlan',
        '$preserveRollbackArtifacts')) {
        Assert-DeploymentTest -Condition $viewerAccess.Contains($requiredText) -Message "Viewer 허용 주소 갱신 계약 누락: $requiredText"
    }

    Write-SswStep 'Viewer 자체 점검 및 바로 가기 rollback 계약 확인'
    foreach ($requiredText in @('BUILD-MANIFEST.json', '-PassThru', 'restore-shortcuts', 'start-menu.lnk', 'startup.lnk')) {
        Assert-DeploymentTest -Condition $viewerInstall.Contains($requiredText) -Message "Viewer 설치 계약 누락: $requiredText"
    }
    $smokeIndex = $viewerInstall.IndexOf('Start-Process -FilePath $viewerExe')
    $backupDeleteIndex = $viewerInstall.IndexOf('Remove-Item -LiteralPath $backup -Recurse -Force')
    Assert-DeploymentTest -Condition ($smokeIndex -ge 0 -and $backupDeleteIndex -gt $smokeIndex) `
        -Message 'Viewer 자체 점검 전에 이전 프로그램 백업을 삭제합니다.'

    Write-SswStep '배포 helper 계약 검사 통과'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
