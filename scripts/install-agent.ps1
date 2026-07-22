param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$AgentId = 'agent-poc-01',
    [string]$SwitchId = 'ACCESS-SW-01',
    [string]$SwitchDisplayName = 'Samsung access switch',
    [string]$SwitchHost = '192.0.2.10',
    [ValidateRange(1, 65535)][int]$SwitchPort = 23,
    [ValidateSet('IES4224GP', 'IES4028XP', 'IES4226XP')][string]$SwitchModel = 'IES4224GP',
    [string]$CredentialId = 'samsung-switch-readonly',
    [string]$UplinkPort = '24',
    [string]$SwitchesJsonPath,
    [ValidateRange(1, 65535)][int]$HttpPort = 18443,
    [ValidateCount(1, 32)][string[]]$ViewerRemoteAddress,
    [switch]$MockMode,
    [switch]$EnableReadOnlyQueries,
    [switch]$DoNotStart,
    [switch]$Preflight,
    [switch]$Repair,
    [switch]$ReuseData
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$firewallRuleName = 'Samsung Switch Watch Agent HTTP'
$legacyFirewallRuleName = 'Samsung Switch Watch Agent HTTPS'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$serviceExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$productionConfig = Join-Path $install 'appsettings.Production.json'
$receiptPath = Join-Path $data 'install-receipt.json'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingFirewallRule = Get-SswAgentFirewallSnapshot -DisplayName $firewallRuleName
$legacyFirewallRule = Get-SswAgentFirewallSnapshot -DisplayName $legacyFirewallRuleName
$existingReceipt = $null
$existingReceiptVersion = 0
$installedEnvironment = @()
$legacyOwnedCertificateThumbprints = @()
$enableReadOnlyQueriesWasSpecified = $PSBoundParameters.ContainsKey('EnableReadOnlyQueries')

function ConvertTo-SswValidatedSwitch {
    param([Parameter(Mandatory = $true)][object]$InputSwitch)

    $forbiddenProperty = @($InputSwitch.PSObject.Properties.Name | Where-Object { $_ -match '(?i)password|secret|token|community|username' }) | Select-Object -First 1
    if ($forbiddenProperty) { throw "스위치 목록 JSON에는 자격 증명 값을 포함할 수 없습니다: $forbiddenProperty" }
    $id = [string]$InputSwitch.Id
    $credential = [string]$InputSwitch.CredentialId
    $displayName = [string]$InputSwitch.DisplayName
    $hostAddress = [string]$InputSwitch.Host
    $uplink = [string]$InputSwitch.UplinkPort
    $model = [string]$InputSwitch.Model
    if ([string]::IsNullOrWhiteSpace($id) -or $id.Length -gt 64 -or $id -notmatch '^[A-Za-z0-9_-]+$') {
        throw '각 Switch Id는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
    }
    if ([string]::IsNullOrWhiteSpace($credential) -or $credential.Length -gt 64 -or $credential -notmatch '^[A-Za-z0-9_-]+$') {
        throw "각 CredentialId는 64자 이하의 안전한 식별자여야 합니다: $id"
    }
    if ([string]::IsNullOrWhiteSpace($displayName) -or $displayName.Length -gt 128 -or $displayName -match '[\x00-\x1F\x7F]') {
        throw "각 DisplayName은 제어문자가 없는 128자 이하 문자열이어야 합니다: $id"
    }
    if ($model -notin @('IES4224GP', 'IES4028XP', 'IES4226XP')) { throw "지원하지 않는 스위치 모델입니다: $id / $model" }
    $parsedAddress = $null
    if (-not [Net.IPAddress]::TryParse($hostAddress, [ref]$parsedAddress) -or
        $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw "각 Host는 유효한 IPv4 주소여야 합니다: $id"
    }
    $portValue = 0
    if (-not [int]::TryParse([string]$InputSwitch.Port, [ref]$portValue) -or $portValue -lt 1 -or $portValue -gt 65535) {
        throw "각 Port는 1~65535 범위여야 합니다: $id"
    }
    if ([string]::IsNullOrWhiteSpace($uplink) -or $uplink.Length -gt 32 -or $uplink -notmatch '^[A-Za-z0-9._/-]+$') {
        throw "각 UplinkPort는 32자 이하의 안전한 포트 식별자여야 합니다: $id"
    }
    return [ordered]@{ Id = $id; DisplayName = $displayName; Model = $model; Host = $parsedAddress.ToString(); Port = $portValue; CredentialId = $credential; UplinkPort = $uplink }
}

function Get-SswLegacyHttpPort {
    param([Parameter(Mandatory = $true)][object]$Config)
    $https = $Config.Agent.PSObject.Properties['Https']
    if ($https -and $https.Value) { return [int]$https.Value.Port }
    $listen = [string]$Config.Agent.ListenUrl
    $uri = $null
    if ([Uri]::TryCreate($listen, [UriKind]::Absolute, [ref]$uri)) { return $uri.Port }
    return 18443
}

function New-SswProductionConfiguration {
    param(
        [Parameter(Mandatory = $true)][object[]]$Switches,
        [AllowNull()][object]$ExistingConfig
    )

    if ($ExistingConfig) {
        $migrated = $ExistingConfig | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        foreach ($removedProperty in @('Https', 'PairingCodeLifetimeMinutes', 'TokenPepper', 'Tokens')) {
            $migrated.Agent.PSObject.Properties.Remove($removedProperty)
        }
        $migrated.Agent | Add-Member -NotePropertyName AgentId -NotePropertyValue $AgentId -Force
        $migrated.Agent | Add-Member -NotePropertyName ListenUrl -NotePropertyValue "http://0.0.0.0:$HttpPort" -Force
        $migrated.Agent | Add-Member -NotePropertyName DataDirectory -NotePropertyValue $data -Force
        $migrated.Agent | Add-Member -NotePropertyName MockMode -NotePropertyValue ([bool]$MockMode) -Force
        $migrated.Agent | Add-Member -NotePropertyName EnableSimulator -NotePropertyValue ([bool]$MockMode) -Force
        $migrated.Agent | Add-Member -NotePropertyName Switches -NotePropertyValue $Switches -Force
        if ($enableReadOnlyQueriesWasSpecified) {
            $migrated.Agent | Add-Member -NotePropertyName EnableReadOnlyQueries -NotePropertyValue $true -Force
        }
        elseif (-not $migrated.Agent.PSObject.Properties['EnableReadOnlyQueries']) {
            $migrated.Agent | Add-Member -NotePropertyName EnableReadOnlyQueries -NotePropertyValue $false -Force
        }
        foreach ($readOnlyQueryDefault in ([ordered]@{
            ReadOnlyQueryMaxCommandLength = 128
            ReadOnlyQueryMaxOutputBytes = 65536
            ReadOnlyQueryRateLimitPerMinute = 12
            ReadOnlyQueryDeviceWaitSeconds = 5
            ReadOnlyQueryTotalTimeoutSeconds = 60
        }).GetEnumerator()) {
            if (-not $migrated.Agent.PSObject.Properties[$readOnlyQueryDefault.Key]) {
                $migrated.Agent | Add-Member -NotePropertyName $readOnlyQueryDefault.Key `
                    -NotePropertyValue $readOnlyQueryDefault.Value
            }
        }
        return $migrated
    }
    return [ordered]@{
        Agent = [ordered]@{
            AgentId = $AgentId
            ListenUrl = "http://0.0.0.0:$HttpPort"
            DataDirectory = $data
            MockMode = [bool]$MockMode
            EnablePolling = $true
            EnableSimulator = [bool]$MockMode
            SchedulerTickSeconds = 1
            EnableReadOnlyQueries = [bool]$EnableReadOnlyQueries
            ReadOnlyQueryMaxCommandLength = 128
            ReadOnlyQueryMaxOutputBytes = 65536
            ReadOnlyQueryRateLimitPerMinute = 12
            ReadOnlyQueryDeviceWaitSeconds = 5
            ReadOnlyQueryTotalTimeoutSeconds = 60
            Retention = [ordered]@{ RawDays = 7; RawMaxMegabytes = 500; EventDays = 90; AuditDays = 180 }
            Switches = $Switches
        }
        Logging = [ordered]@{ LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
        AllowedHosts = '*'
    }
}

Write-SswStep '설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Agent는 Windows x64에서만 설치할 수 있습니다.' }
Assert-SswAdministrator
if ([string]::IsNullOrWhiteSpace($AgentId) -or $AgentId.Length -gt 64 -or $AgentId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'AgentId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
$viewerRemoteAddresses = @()
$singleSwitchParameters = @('SwitchId', 'SwitchDisplayName', 'SwitchHost', 'SwitchPort', 'SwitchModel', 'CredentialId', 'UplinkPort')
if (-not [string]::IsNullOrWhiteSpace($SwitchesJsonPath) -and @($singleSwitchParameters | Where-Object { $PSBoundParameters.ContainsKey($_) }).Count -gt 0) {
    throw '-SwitchesJsonPath는 단일 스위치 파라미터와 함께 사용할 수 없습니다.'
}
if ($Repair -and -not [string]::IsNullOrWhiteSpace($SwitchesJsonPath)) { throw '-Repair는 기존 스위치 목록을 유지하므로 -SwitchesJsonPath를 받을 수 없습니다.' }

if (-not [string]::IsNullOrWhiteSpace($SwitchesJsonPath)) {
    $switchListPath = [IO.Path]::GetFullPath($SwitchesJsonPath)
    if (-not (Test-Path -LiteralPath $switchListPath -PathType Leaf)) { throw "스위치 목록 JSON을 찾지 못했습니다: $switchListPath" }
    if ((Get-Item -LiteralPath $switchListPath).Length -gt 1MB) { throw '스위치 목록 JSON은 1MB를 초과할 수 없습니다.' }
    $switchJson = Get-Content -LiteralPath $switchListPath -Raw -Encoding UTF8
    if (-not $switchJson.TrimStart().StartsWith('[') -or -not $switchJson.TrimEnd().EndsWith(']')) { throw '스위치 목록 JSON의 최상위 값은 배열이어야 합니다.' }
    try { $switchInput = @($switchJson | ConvertFrom-Json) }
    catch { throw "스위치 목록 JSON을 읽지 못했습니다: $($_.Exception.Message)" }
    if ($switchInput.Count -lt 1 -or $switchInput.Count -gt 256) { throw '스위치 목록은 1~256대여야 합니다.' }
    $switchConfigurations = @($switchInput | ForEach-Object { ConvertTo-SswValidatedSwitch -InputSwitch $_ })
}
else {
    $singleSwitch = [pscustomobject]@{ Id = $SwitchId; DisplayName = $SwitchDisplayName; Model = $SwitchModel; Host = $SwitchHost; Port = $SwitchPort; CredentialId = $CredentialId; UplinkPort = $UplinkPort }
    $switchConfigurations = @((ConvertTo-SswValidatedSwitch -InputSwitch $singleSwitch))
}

$duplicateSwitchId = $switchConfigurations | Group-Object { ([string]$_.Id).ToUpperInvariant() } | Where-Object Count -gt 1 | Select-Object -First 1
if ($duplicateSwitchId) { throw "중복 Switch Id가 있습니다: $($duplicateSwitchId.Name)" }
$switchInventoryHash = Get-SswSwitchInventoryHash -Switches $switchConfigurations
$SwitchId = [string]$switchConfigurations[0].Id
$CredentialId = [string]$switchConfigurations[0].CredentialId

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Agent 배포 파일을 찾지 못했습니다: $sourceExe" }
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { throw "패키지 빌드 매니페스트를 찾지 못했습니다: $sourceManifestPath" }
try { $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "패키지 빌드 매니페스트를 읽지 못했습니다: $($_.Exception.Message)" }
if ($sourceManifest.packageKind -ne 'Agent' -or $sourceManifest.executable.name -ne 'SamsungSwitchWatch.Agent.exe') { throw 'Agent 패키지 매니페스트 형식이 올바르지 않습니다.' }
$sourceExeHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceExeHash -ne ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) { throw 'Agent 실행 파일이 빌드 매니페스트의 SHA-256과 일치하지 않습니다.' }
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'

if ($Repair -and -not $existingService) { throw "복구할 서비스가 없습니다: $serviceName" }
if (-not $Repair -and $existingService) { throw "서비스가 이미 설치되어 있습니다. 복구 설치에는 -Repair를 사용하세요: $serviceName" }
if (-not $Repair -and (Test-Path -LiteralPath $install)) { throw "설치 폴더가 이미 있습니다. 흔적을 확인하거나 제거 후 다시 시도하세요: $install" }
if ($existingFirewallRule -and -not (Test-SswOwnedAgentFirewallRule -Snapshot $existingFirewallRule)) { throw '동일한 HTTP 방화벽 규칙에 제품 소유권 표식이 없어 자동 변경하지 않습니다.' }
if ($legacyFirewallRule -and -not (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $legacyFirewallRule)) { throw '동일한 기존 HTTPS 방화벽 규칙에 제품 소유권 표식이 없어 자동 변경하지 않습니다.' }
if (-not $Repair -and ($existingFirewallRule -or $legacyFirewallRule)) { throw '동일한 제품 방화벽 규칙이 이미 있습니다. 기존 설치 흔적을 확인하세요.' }

$existingDataEntries = if (Test-Path -LiteralPath $data -PathType Container) { @(Get-ChildItem -LiteralPath $data -Force) } else { @() }
if (-not $Repair -and $existingDataEntries.Count -gt 0) {
    if (-not $ReuseData) { throw "비어 있지 않은 기존 데이터 폴더는 명시적인 -ReuseData와 유효한 설치 영수증이 필요합니다: $data" }
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw '기존 데이터의 install-receipt.json이 없어 안전하게 재사용할 수 없습니다.' }
    try { $existingReceipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "기존 데이터 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
    $existingReceiptVersion = Assert-SswAgentInstallReceipt -Receipt $existingReceipt -AgentId $AgentId `
        -SwitchInventoryHash $switchInventoryHash -SwitchCount $switchConfigurations.Count
}
elseif (-not $Repair -and $ReuseData -and $existingDataEntries.Count -eq 0) { Write-Host '기존 데이터가 비어 있어 -ReuseData 확인은 필요하지 않습니다.' }

$installedConfig = $null
if ($Repair) {
    if (-not (Test-Path -LiteralPath $productionConfig -PathType Leaf)) { throw "설치 설정을 찾지 못했습니다: $productionConfig" }
    try { $installedConfig = Get-Content -LiteralPath $productionConfig -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "설치 설정을 읽지 못했습니다: $($_.Exception.Message)" }
    $installedDataDirectory = [IO.Path]::GetFullPath([string]$installedConfig.Agent.DataDirectory)
    Assert-SswProductPath -Path $installedDataDirectory -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
    if ($PSBoundParameters.ContainsKey('DataDirectory') -and
        -not $data.Equals($installedDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw '-Repair의 DataDirectory는 기존 설치 설정과 정확히 일치해야 합니다.'
    }
    $data = $installedDataDirectory
    $receiptPath = Join-Path $data 'install-receipt.json'
    if (-not $PSBoundParameters.ContainsKey('HttpPort')) { $HttpPort = Get-SswLegacyHttpPort -Config $installedConfig }
    $AgentId = [string]$installedConfig.Agent.AgentId
    $switchConfigurations = @(@($installedConfig.Agent.Switches) | ForEach-Object { ConvertTo-SswValidatedSwitch -InputSwitch $_ })
    $switchInventoryHash = Get-SswSwitchInventoryHash -Switches $switchConfigurations
    $SwitchId = [string]$switchConfigurations[0].Id
    $CredentialId = [string]$switchConfigurations[0].CredentialId
    $mockProperty = $installedConfig.Agent.PSObject.Properties['MockMode']
    if (-not $PSBoundParameters.ContainsKey('MockMode') -and $mockProperty) { $MockMode = [bool]$mockProperty.Value }
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "기존 설치 영수증을 찾지 못했습니다: $receiptPath"
    }
    try { $existingReceipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "기존 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
    $existingReceiptVersion = Assert-SswAgentInstallReceipt -Receipt $existingReceipt -AgentId $AgentId `
        -SwitchInventoryHash $switchInventoryHash -SwitchCount $switchConfigurations.Count
    if (-not $PSBoundParameters.ContainsKey('ViewerRemoteAddress')) {
        $receiptAddresses = if ($existingReceiptVersion -eq 2 -and $existingReceipt.PSObject.Properties['viewerRemoteAddresses']) {
            @($existingReceipt.viewerRemoteAddresses)
        }
        else { @() }
        if ($receiptAddresses.Count -gt 0) { $ViewerRemoteAddress = $receiptAddresses }
        elseif ($existingFirewallRule) { $ViewerRemoteAddress = @($existingFirewallRule.RemoteAddress) }
        elseif ($legacyFirewallRule) { $ViewerRemoteAddress = @($legacyFirewallRule.RemoteAddress) }
    }
    $serviceProperties = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -ErrorAction SilentlyContinue
    $installedEnvironment = if ($serviceProperties) { @($serviceProperties.Environment) } else { @() }

    if ($existingReceiptVersion -eq 1) {
        $legacyOwnedCertificateThumbprints = @(Get-SswLegacyOwnedAgentCertificateThumbprints `
            -Receipt $existingReceipt -Configuration $installedConfig)
    }
}
elseif (-not (Test-SswTcpPortAvailable -Port $HttpPort)) { throw "Agent HTTP 포트를 이미 다른 프로세스가 사용하고 있습니다: $HttpPort" }

if ($null -eq $ViewerRemoteAddress -or @($ViewerRemoteAddress).Count -eq 0) {
    throw '방화벽에서 허용할 Viewer PC 고정 IPv4 주소를 -ViewerRemoteAddress로 1~32개 지정해야 합니다.'
}
$viewerRemoteAddresses = @(ConvertTo-SswViewerRemoteAddresses -Address $ViewerRemoteAddress)
if ($existingFirewallRule -and $legacyFirewallRule) {
    throw 'HTTP와 기존 HTTPS 제품 방화벽 규칙이 동시에 존재합니다. 중복 규칙을 먼저 점검하세요.'
}
Assert-SswAgentFirewallGateReady -Port $HttpPort -AgentExecutablePath $serviceExe

if (-not $MockMode -and -not $Repair -and @($switchConfigurations | Where-Object { $_.Host -eq '192.0.2.10' }).Count -gt 0) {
    throw '실환경 설치에는 -SwitchHost로 실제 관리 주소를 지정해야 합니다.'
}

Write-Host "  source  : $source"
Write-Host "  install : $install"
Write-Host "  data    : $data"
Write-Host "  service : $serviceName (고정)"
Write-Host "  HTTP    : TCP/$HttpPort (암호화·인증 없음)"
Write-Host "  Viewer  : $($viewerRemoteAddresses -join ', ')"
Write-Host "  조회 명령: $(if ($Repair -and -not $enableReadOnlyQueriesWasSpecified) { '기존 설정 보존' } elseif ($EnableReadOnlyQueries) { '사용' } else { '사용 안 함(기본값)' })"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 시스템은 변경되지 않았습니다.'
    return
}

$installParent = Split-Path $install -Parent
$transactionId = [Guid]::NewGuid().ToString('N')
$staging = "$install.__staging_$transactionId"
$backup = "$install.__backup_$transactionId"
$operationRoot = Join-Path $env:ProgramData 'SamsungSwitchWatch-Operations'
$journalPath = Join-Path $operationRoot 'agent-install.json'
$transactionDataBackup = Join-Path $operationRoot "transactions\$transactionId"
$serviceCreated = $false
$serviceEnvironmentChanged = $false
$firewallChanged = $false
$dataCreated = $false
$installSwapped = $false
$previousServiceWasRunning = $existingService -and $existingService.Status -eq 'Running'
$dataAclSnapshot = @()
$dataAclChanged = $false
$databaseSnapshotTaken = $false
$databaseFiles = @('switchwatch.db', 'switchwatch.db-wal', 'switchwatch.db-shm')
$databaseExistence = @{}
$schemaBackupsBefore = @()
$receiptExistedBefore = Test-Path -LiteralPath $receiptPath -PathType Leaf
$receiptBackupPath = Join-Path $transactionDataBackup 'install-receipt.json'
$receiptTemporary = "$receiptPath.$transactionId.tmp"
$receiptReplaceBackup = "$receiptPath.$transactionId.replace.bak"
$transactionCommitted = $false

Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep '검증된 임시 폴더에 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notin @('data', 'certs') } |
        Copy-Item -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'SamsungSwitchWatch.Agent.exe') -PathType Leaf)) { throw '임시 폴더의 Agent 실행 파일 검증에 실패했습니다.' }
    $configuration = New-SswProductionConfiguration -Switches $switchConfigurations -ExistingConfig $installedConfig
    [IO.File]::WriteAllText((Join-Path $staging 'appsettings.Production.json'),
        ($configuration | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))

    if (-not (Test-Path -LiteralPath $data)) {
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $dataCreated = $true
    }
    if ($Repair -and $previousServiceWasRunning) {
        Write-SswStep '기존 Agent 서비스 정지'
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    if ($Repair -or $ReuseData) {
        Write-SswStep '데이터베이스 트랜잭션 파일 및 설치 영수증 백업'
        New-Item -ItemType Directory -Path $transactionDataBackup -Force | Out-Null
        Set-SswInstallerBackupAcl -Path $transactionDataBackup
        foreach ($databaseFile in $databaseFiles) {
            $databasePath = Join-Path $data $databaseFile
            $databaseExistence[$databaseFile] = Test-Path -LiteralPath $databasePath -PathType Leaf
            if ($databaseExistence[$databaseFile]) { Copy-Item -LiteralPath $databasePath -Destination (Join-Path $transactionDataBackup $databaseFile) -Force }
        }
        $schemaBackupsBefore = @(Get-ChildItem -LiteralPath $data -Filter 'switchwatch.db.schema-*.bak' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        if ($receiptExistedBefore) { Copy-Item -LiteralPath $receiptPath -Destination $receiptBackupPath -Force }
        $databaseSnapshotTaken = $true
        Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
            -Stage 'data-backed-up' -Status 'running' -Version ([string]$sourceManifest.version)
    }
    if (-not $dataCreated) { $dataAclSnapshot = @(Get-SswDirectoryAclSnapshot -Path $data) }

    Write-SswStep 'Agent 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    if (-not $Repair) {
        Write-SswStep 'Windows 서비스 등록'
        & sc.exe create $serviceName "binPath= `"$serviceExe`"" 'start= auto' 'obj= NT AUTHORITY\LocalService' `
            'DisplayName= Samsung Switch Watch Agent' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 등록에 실패했습니다.' }
        $serviceCreated = $true
    }
    else {
        & sc.exe config $serviceName "binPath= `"$serviceExe`"" 'start= auto' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 실행 경로 갱신에 실패했습니다.' }
    }
    & sc.exe description $serviceName 'Samsung iES read-only monitoring Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 설명 설정에 실패했습니다.' }
    & sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 복구 정책 설정에 실패했습니다.' }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '서비스 SID 활성화에 실패했습니다.' }

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $nextEnvironment = @($installedEnvironment | Where-Object {
        $_ -notlike 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' -and $_ -notlike 'DOTNET_ENVIRONMENT=*'
    }) + @('DOTNET_ENVIRONMENT=Production')
    if (-not $Repair) {
        New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Force -Value $nextEnvironment | Out-Null
        $serviceEnvironmentChanged = $true
    }

    Write-SswStep '서비스 SID 기준 최소 폴더 권한 설정'
    $serviceSid = Get-SswServiceSid -Name $serviceName
    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute
    $dataAclChanged = $true
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify

    $firewallIsExact = $existingFirewallRule -and
        (Test-SswAgentFirewallRuleExact -Snapshot $existingFirewallRule -Port $HttpPort -RemoteAddress $viewerRemoteAddresses)
    if (-not $firewallIsExact) {
        Write-SswStep '허용 Viewer IPv4만 받는 HTTP 방화벽 규칙 적용'
        $firewallChanged = $true
        if ($existingFirewallRule) { Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' }
        New-SswAgentFirewallRule -Port $HttpPort -RemoteAddress $viewerRemoteAddresses
    }
    if ($legacyFirewallRule) {
        $firewallChanged = $true
        Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https'
    }
    Assert-SswAgentFirewallGateReady -Port $HttpPort -AgentExecutablePath $serviceExe
    $appliedFirewallRule = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
    if (-not $appliedFirewallRule -or
        -not (Test-SswAgentFirewallRuleExact -Snapshot $appliedFirewallRule -Port $HttpPort `
            -RemoteAddress $viewerRemoteAddresses)) {
        throw '적용된 Agent HTTP 방화벽 규칙이 설치 요청과 정확히 일치하지 않습니다.'
    }

    # Repair는 이전 실행 상태와 -DoNotStart 여부와 관계없이 새 바이너리와 DB schema를 검증합니다.
    $shouldStart = $Repair -or (-not $DoNotStart -and $MockMode)
    if ($shouldStart) {
        Write-SswStep 'Agent 서비스 시작 및 HTTP readiness 확인'
        Start-Service -Name $serviceName
        $readinessTimeoutSeconds = if ($MockMode) { 45 } else { 210 }
        $healthStatus = Invoke-SswLocalHealthProbe -Port $HttpPort -TimeoutSeconds $readinessTimeoutSeconds
        Write-Host "  readiness: $healthStatus"
        if ($Repair) {
            Stop-Service -Name $serviceName -Force
            (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            Write-SswStep '검증 성공 후 구 인증서 서비스 secret 제거'
            New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Force -Value $nextEnvironment | Out-Null
            $serviceEnvironmentChanged = $true
            if ($previousServiceWasRunning -and -not $DoNotStart) {
                Start-Service -Name $serviceName
                $healthStatus = Invoke-SswLocalHealthProbe -Port $HttpPort -TimeoutSeconds $readinessTimeoutSeconds
                Write-Host "  readiness (clean environment): $healthStatus"
            }
            else { Write-Host '  service  : 검증 후 이전 중지 상태로 복원' }
        }
    }
    elseif (-not $Repair -and -not $MockMode) { Write-Host '실환경 Agent는 자격 증명 저장 전까지 시작하지 않습니다.' }

    $receipt = [ordered]@{
        receiptVersion = 2
        product = 'SamsungSwitchWatchAgent'
        agentId = $AgentId
        switchId = $SwitchId
        credentialId = $CredentialId
        switchCount = $switchConfigurations.Count
        switchInventoryHash = $switchInventoryHash
        httpPort = $HttpPort
        viewerRemoteAddresses = $viewerRemoteAddresses
        installedVersion = [string]$sourceManifest.version
        sourceCommit = [string]$sourceManifest.sourceCommit
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($receiptTemporary, $receipt, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        [IO.File]::Replace($receiptTemporary, $receiptPath, $receiptReplaceBackup, $true)
        if (Test-Path -LiteralPath $receiptReplaceBackup -PathType Leaf) { Remove-Item -LiteralPath $receiptReplaceBackup -Force }
    }
    else { Move-Item -LiteralPath $receiptTemporary -Destination $receiptPath }

    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
        -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true

    # 인증서는 서비스, HTTP listener, DB migration, receipt가 모두 검증된 뒤 마지막으로 제거합니다.
    foreach ($legacyOwnedCertificateThumbprint in $legacyOwnedCertificateThumbprints) {
        $legacyCertificatePath = "Cert:\LocalMachine\My\$legacyOwnedCertificateThumbprint"
        if (Test-Path -LiteralPath $legacyCertificatePath) {
            try { Remove-Item -LiteralPath $legacyCertificatePath -Force -ErrorAction Stop }
            catch {
                # 비내보내기 개인 키는 rollback할 수 없으므로 commit 뒤 정리 실패가 설치 rollback을 유발하면 안 됩니다.
                Write-Warning "설치는 완료됐지만 구 설치기 소유 인증서를 제거하지 못했습니다: $legacyOwnedCertificateThumbprint" `
                    -WarningAction Continue
            }
        }
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $transactionDataBackup) { Remove-Item -LiteralPath $transactionDataBackup -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ''
    Write-Host '설치가 완료되었습니다.' -ForegroundColor Green
    Write-Warning 'Agent API는 암호화·인증이 없습니다. 위 고정 Viewer IPv4 방화벽 범위를 유지하십시오.' `
        -WarningAction Continue
    if (-not $Repair) {
        Write-Host "자격 증명 ID: $CredentialId"
        if (-not $MockMode) { Write-Host '다음 단계: 관리자 PowerShell에서 set-switch-credential.ps1을 실행하세요.' }
    }
}
catch {
    $failure = $_
    if ($transactionCommitted) {
        Write-Warning "설치는 이미 commit됐으며 post-commit 정리 중 오류가 발생했습니다. 자동 rollback하지 않습니다: $($failure.Exception.Message)" `
            -WarningAction Continue
        return
    }
    Write-Warning '설치 실패를 감지해 변경 사항을 복구합니다.' -WarningAction Continue
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-service'; Action = {
            $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($currentService -and $currentService.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force
                $currentService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            }
        } },
        [pscustomobject]@{ Name = 'delete-new-service'; Action = {
            if ($serviceCreated) {
                & sc.exe delete $serviceName | Out-Null
                if ($LASTEXITCODE -ne 0) { throw '서비스 제거 요청 실패' }
                Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
            }
        } },
        [pscustomobject]@{ Name = 'restore-service-environment'; Action = {
            if ($serviceEnvironmentChanged -and -not $serviceCreated) {
                New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment `
                    -PropertyType MultiString -Force -Value @($installedEnvironment) | Out-Null
            }
        } },
        [pscustomobject]@{ Name = 'restore-database'; Action = {
            if ($databaseSnapshotTaken -and -not $dataCreated) {
                foreach ($databaseFile in $databaseFiles) {
                    $databasePath = Join-Path $data $databaseFile
                    if (Test-Path -LiteralPath $databasePath -PathType Leaf) { Remove-Item -LiteralPath $databasePath -Force }
                    if ([bool]$databaseExistence[$databaseFile]) { Copy-Item -LiteralPath (Join-Path $transactionDataBackup $databaseFile) -Destination $databasePath -Force }
                }
                foreach ($schemaBackup in @(Get-ChildItem -LiteralPath $data -Filter 'switchwatch.db.schema-*.bak' -File -ErrorAction SilentlyContinue)) {
                    if ($schemaBackup.FullName -notin $schemaBackupsBefore) { Remove-Item -LiteralPath $schemaBackup.FullName -Force }
                }
                if ($receiptExistedBefore) { Copy-Item -LiteralPath $receiptBackupPath -Destination $receiptPath -Force }
                elseif (Test-Path -LiteralPath $receiptPath -PathType Leaf) { Remove-Item -LiteralPath $receiptPath -Force }
            }
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restore-firewall'; Action = {
            if ($firewallChanged) {
                Restore-SswAgentFirewallSnapshot -Snapshot $existingFirewallRule
                if ($legacyFirewallRule) { Restore-SswAgentFirewallSnapshot -Snapshot $legacyFirewallRule }
            }
        } },
        [pscustomobject]@{ Name = 'restore-data'; Action = {
            if ($dataCreated -and (Test-Path -LiteralPath $data)) { Remove-Item -LiteralPath $data -Recurse -Force }
            elseif ($dataAclChanged -and $dataAclSnapshot.Count -gt 0) { Restore-SswDirectoryAclSnapshot -Snapshot $dataAclSnapshot }
            foreach ($receiptArtifact in @($receiptTemporary, $receiptReplaceBackup)) {
                if (Test-Path -LiteralPath $receiptArtifact -PathType Leaf) { Remove-Item -LiteralPath $receiptArtifact -Force }
            }
            if (Test-Path -LiteralPath $transactionDataBackup) { Remove-Item -LiteralPath $transactionDataBackup -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restart-previous-service'; Action = {
            if ($Repair -and $previousServiceWasRunning -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { Start-Service -Name $serviceName }
        } }
    ))
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
        -Stage 'rollback-completed' -Status 'failed' -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) {
        Write-Warning ("일부 자동 복구 단계가 실패했습니다: {0}" -f ($rollbackErrors -join ', ')) -WarningAction Continue
    }
    throw $failure
}
