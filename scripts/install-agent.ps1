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
    [ValidateRange(1, 65535)][int]$HttpsPort = 18443,
    [string]$ViewerRemoteAddress,
    [switch]$MockMode,
    [switch]$SkipFirewall,
    [switch]$DoNotStart,
    [switch]$Preflight,
    [switch]$Repair,
    [switch]$ReuseData,
    [switch]$RotateCertificate,
    [string]$RotationCertificateThumbprint,
    [ValidateRange(1, 14)][int]$CertificateOverlapDays = 7
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$firewallRuleName = 'Samsung Switch Watch Agent HTTPS'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$serviceExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$productionConfig = Join-Path $install 'appsettings.Production.json'
$certificatePath = Join-Path $install 'certs\agent.pfx'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingFirewallRule = Get-SswAgentFirewallSnapshot -DisplayName $firewallRuleName
$receiptPath = Join-Path $data 'install-receipt.json'
$existingCertificatePassword = $null
$existingStoreThumbprint = $null
$existingCertificateFingerprint = $null
$newStoreCertificate = $null
$newStoreCertificateCreated = $false
$existingCertificateOwnedByInstaller = $false
$activeCertificateFingerprint = $null
$activeStoreThumbprint = $null
$activeCertificateOwnedByInstaller = $false
$grantActiveCertificateKey = $false

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
    return [ordered]@{ Id = $id; DisplayName = $displayName; Model = $model; Host = $hostAddress; Port = $portValue; CredentialId = $credential; UplinkPort = $uplink }
}

function Get-SswSwitchInventoryHash {
    param([Parameter(Mandatory = $true)][object[]]$Switches)
    $canonical = $Switches | ConvertTo-Json -Depth 6 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

Write-SswStep '설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Agent는 Windows x64에서만 설치할 수 있습니다.' }
if (-not (Test-SswAdministrator)) { throw '관리자 권한 PowerShell에서 실행해야 합니다.' }
if ([string]::IsNullOrWhiteSpace($AgentId) -or $AgentId.Length -gt 64 -or $AgentId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'AgentId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
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
    if (-not $switchJson.TrimStart().StartsWith('[') -or -not $switchJson.TrimEnd().EndsWith(']')) {
        throw '스위치 목록 JSON의 최상위 값은 배열이어야 합니다.'
    }
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
if (-not $SkipFirewall -and -not [string]::IsNullOrWhiteSpace($ViewerRemoteAddress)) {
    $viewerAddress = $null
    if (-not [Net.IPAddress]::TryParse($ViewerRemoteAddress, [ref]$viewerAddress) -or
        $viewerAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'ViewerRemoteAddress는 방화벽에서 허용할 단일 IPv4 주소여야 합니다.'
    }
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Agent 배포 파일을 찾지 못했습니다: $sourceExe" }
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { throw "패키지 빌드 매니페스트를 찾지 못했습니다: $sourceManifestPath" }
try { $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "패키지 빌드 매니페스트를 읽지 못했습니다: $($_.Exception.Message)" }
if ($sourceManifest.packageKind -ne 'Agent' -or $sourceManifest.executable.name -ne 'SamsungSwitchWatch.Agent.exe') {
    throw 'Agent 패키지 매니페스트 형식이 올바르지 않습니다.'
}
$sourceExeHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceExeHash -ne ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) {
    throw 'Agent 실행 파일이 빌드 매니페스트의 SHA-256과 일치하지 않습니다.'
}
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
if ($Repair -and -not $existingService) { throw "복구할 서비스가 없습니다: $serviceName" }
if ($RotateCertificate -and -not $Repair) { throw '-RotateCertificate는 기존 Agent의 -Repair 설치에서만 사용할 수 있습니다.' }
if ($RotateCertificate -and ([string]$RotationCertificateThumbprint).Replace(' ', '') -notmatch '^[0-9A-Fa-f]{40}$') {
    throw '-RotateCertificate에는 new-agent-certificate.ps1로 미리 준비한 -RotationCertificateThumbprint가 필요합니다.'
}
if (-not $RotateCertificate -and -not [string]::IsNullOrWhiteSpace($RotationCertificateThumbprint)) {
    throw '-RotationCertificateThumbprint는 -RotateCertificate와 함께 사용해야 합니다.'
}
if (-not $Repair -and $existingService) { throw "서비스가 이미 설치되어 있습니다. 복구 설치에는 -Repair를 사용하세요: $serviceName" }
if (-not $Repair -and (Test-Path -LiteralPath $install)) {
    throw "설치 폴더가 이미 있습니다. 흔적을 확인하거나 제거 후 다시 시도하세요: $install"
}
$existingDataEntries = if (Test-Path -LiteralPath $data -PathType Container) {
    @(Get-ChildItem -LiteralPath $data -Force)
} else { @() }
if (-not $Repair -and $existingDataEntries.Count -gt 0) {
    if (-not $ReuseData) {
        throw "비어 있지 않은 기존 데이터 폴더는 명시적인 -ReuseData와 유효한 설치 영수증이 필요합니다: $data"
    }
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw '기존 데이터의 install-receipt.json이 없어 안전하게 재사용할 수 없습니다.'
    }
    try { $existingReceipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "기존 데이터 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
    $receiptInventoryProperty = $existingReceipt.PSObject.Properties['switchInventoryHash']
    $receiptInventoryMatches = if ($receiptInventoryProperty -and -not [string]::IsNullOrWhiteSpace([string]$receiptInventoryProperty.Value)) {
        [string]$receiptInventoryProperty.Value -eq $switchInventoryHash
    }
    else {
        $switchConfigurations.Count -eq 1 -and $existingReceipt.switchId -eq $SwitchId -and $existingReceipt.credentialId -eq $CredentialId
    }
    if ($existingReceipt.product -ne 'SamsungSwitchWatchAgent' -or $existingReceipt.receiptVersion -ne 1 -or
        $existingReceipt.agentId -ne $AgentId -or -not $receiptInventoryMatches) {
        throw '기존 데이터 설치 영수증이 요청한 Agent/스위치 인벤토리와 일치하지 않습니다.'
    }
}
elseif (-not $Repair -and $ReuseData -and $existingDataEntries.Count -eq 0) {
    Write-Host '기존 데이터가 비어 있어 -ReuseData 확인은 필요하지 않습니다.'
}
if (-not $MockMode -and -not $Repair -and @($switchConfigurations | Where-Object { $_.Host -eq '192.0.2.10' }).Count -gt 0) {
    throw '실환경 설치에는 -SwitchHost로 실제 관리 주소를 지정해야 합니다.'
}
if (-not $SkipFirewall -and -not $existingFirewallRule -and [string]::IsNullOrWhiteSpace($ViewerRemoteAddress)) {
    throw '방화벽을 열 Viewer PC 주소를 -ViewerRemoteAddress로 지정하거나 -SkipFirewall을 사용하세요.'
}
if (-not $Repair -and $existingFirewallRule) {
    throw "동일한 방화벽 규칙이 이미 있습니다. 기존 설치 흔적을 확인하세요: $firewallRuleName"
}
if ($Repair) {
    if (-not (Test-Path -LiteralPath $productionConfig -PathType Leaf)) { throw "설치 설정을 찾지 못했습니다: $productionConfig" }
    try { $installedConfig = Get-Content -LiteralPath $productionConfig -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "설치 설정을 읽지 못했습니다: $($_.Exception.Message)" }
    $HttpsPort = [int]$installedConfig.Agent.Https.Port
    $AgentId = [string]$installedConfig.Agent.AgentId
    $switchConfigurations = @(@($installedConfig.Agent.Switches) | ForEach-Object { ConvertTo-SswValidatedSwitch -InputSwitch $_ })
    $switchInventoryHash = Get-SswSwitchInventoryHash -Switches $switchConfigurations
    $installedSwitch = $switchConfigurations[0]
    if ($installedSwitch) {
        $SwitchId = [string]$installedSwitch.Id
        $CredentialId = [string]$installedSwitch.CredentialId
    }
    $serviceProperties = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" `
        -Name Environment -ErrorAction SilentlyContinue
    $installedEnvironment = if ($serviceProperties) { $serviceProperties.Environment } else { @() }
    $storeThumbprintProperty = $installedConfig.Agent.Https.PSObject.Properties['CertificateStoreThumbprint']
    $existingStoreThumbprint = if ($storeThumbprintProperty) { ([string]$storeThumbprintProperty.Value).Replace(' ', '').ToUpperInvariant() } else { '' }
    if (-not [string]::IsNullOrWhiteSpace($existingStoreThumbprint)) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::My,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $matches = $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $existingStoreThumbprint, $false)
            if ($matches.Count -ne 1 -or -not $matches[0].HasPrivateKey) { throw '기존 LocalMachine 인증서를 찾지 못했거나 개인 키가 없습니다.' }
            $existingCertificateOwnedByInstaller = $matches[0].FriendlyName -like 'Samsung Switch Watch Agent *'
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $existingCertificateFingerprint = ([BitConverter]::ToString($sha256.ComputeHash($matches[0].RawData))).Replace('-', '') }
            finally { $sha256.Dispose() }
        }
        finally { $store.Dispose() }
    }
    else {
        if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) { throw "기존 PFX 인증서를 찾지 못했습니다: $certificatePath" }
        $certificateEnvironmentLine = $installedEnvironment | Where-Object { $_ -like 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' } | Select-Object -First 1
        if (-not $certificateEnvironmentLine) {
            throw '기존 PFX 인증서 암호 환경 변수가 없습니다. 자동 복구 설치를 중단합니다.'
        }
        $existingCertificatePassword = ([string]$certificateEnvironmentLine).Substring('SAMSUNG_SWITCH_WATCH_CERT_PASSWORD='.Length)
        $legacyCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 `
            -ArgumentList $certificatePath, $existingCertificatePassword
        try {
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $existingCertificateFingerprint = ([BitConverter]::ToString($sha256.ComputeHash($legacyCertificate.RawData))).Replace('-', '') }
            finally { $sha256.Dispose() }
        }
        finally { $legacyCertificate.Dispose() }
    }
    if (-not $SkipFirewall -and $existingFirewallRule) {
        $legacyOwned = $existingFirewallRule.Name -ne 'SamsungSwitchWatchAgent-Https' -and
            [string]::IsNullOrWhiteSpace([string]$existingFirewallRule.Group) -and
            [string]::IsNullOrWhiteSpace([string]$existingFirewallRule.Description)
        if (-not (Test-SswOwnedAgentFirewallRule -Snapshot $existingFirewallRule) -and -not $legacyOwned) {
            throw '동일 표시 이름의 방화벽 규칙에 제품 소유권 표식이 없어 자동 변경하지 않습니다.'
        }
        if ([string]::IsNullOrWhiteSpace($ViewerRemoteAddress) -and $existingFirewallRule.RemoteAddress.Count -eq 1) {
            $ViewerRemoteAddress = [string]$existingFirewallRule.RemoteAddress[0]
        }
        if ([string]::IsNullOrWhiteSpace($ViewerRemoteAddress)) {
            throw '방화벽 규칙 복구에는 허용할 단일 Viewer IPv4 주소가 필요합니다.'
        }
    }
}
elseif (-not (Test-SswTcpPortAvailable -Port $HttpsPort)) {
    throw "Agent HTTPS 포트를 이미 다른 프로세스가 사용하고 있습니다: $HttpsPort"
}
if (-not $SkipFirewall) {
    $validatedViewerAddress = $null
    if (-not [Net.IPAddress]::TryParse($ViewerRemoteAddress, [ref]$validatedViewerAddress) -or
        $validatedViewerAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw '방화벽 규칙에는 단일 Viewer IPv4 주소가 필요합니다.'
    }
}

Write-Host "  source  : $source"
Write-Host "  install : $install"
Write-Host "  data    : $data"
Write-Host "  service : $serviceName (고정)"
Write-Host "  HTTPS   : TCP/$HttpsPort"
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
$firewallChanged = $false
$dataCreated = $false
$installSwapped = $false
$previousServiceWasRunning = $existingService -and $existingService.Status -eq 'Running'
$pfxPasswordText = $existingCertificatePassword
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

Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

function New-RandomBase64([int]$ByteCount) {
    $bytes = New-Object byte[] $ByteCount
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        $rng.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

try {
    Write-SswStep '검증된 임시 폴더에 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notin @('data', 'certs') } |
        Copy-Item -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'SamsungSwitchWatch.Agent.exe') -PathType Leaf)) {
        throw '임시 폴더의 Agent 실행 파일 검증에 실패했습니다.'
    }

    if ($Repair) {
        $stagingConfigPath = Join-Path $staging 'appsettings.Production.json'
        Copy-Item -LiteralPath $productionConfig -Destination $stagingConfigPath -Force
        if (Test-Path -LiteralPath (Join-Path $install 'certs') -PathType Container) {
            Copy-Item -LiteralPath (Join-Path $install 'certs') -Destination $staging -Recurse -Force
        }
        $activeCertificateFingerprint = $existingCertificateFingerprint
        $activeStoreThumbprint = $existingStoreThumbprint
        $activeCertificateOwnedByInstaller = $existingCertificateOwnedByInstaller
        if ($RotateCertificate) {
            $rotationThumbprint = $RotationCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
            $rotationStore = [Security.Cryptography.X509Certificates.X509Store]::new(
                [Security.Cryptography.X509Certificates.StoreName]::My,
                [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
            try {
                $rotationStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                $rotationMatches = $rotationStore.Certificates.Find(
                    [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $rotationThumbprint, $false)
                if ($rotationMatches.Count -ne 1 -or -not $rotationMatches[0].HasPrivateKey -or $rotationMatches[0].NotAfter -le (Get-Date)) {
                    throw '미리 준비한 회전 인증서를 찾지 못했거나 개인 키/유효기간이 올바르지 않습니다.'
                }
                $newStoreCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rotationMatches[0])
            }
            finally { $rotationStore.Dispose() }
            $grantActiveCertificateKey = $true
            $activeStoreThumbprint = $newStoreCertificate.Thumbprint
            $activeCertificateOwnedByInstaller = $newStoreCertificate.FriendlyName -like 'Samsung Switch Watch Agent *'
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $activeCertificateFingerprint = ([BitConverter]::ToString($sha256.ComputeHash($newStoreCertificate.RawData))).Replace('-', '') }
            finally { $sha256.Dispose() }
            $rotatedConfig = Get-Content -LiteralPath $stagingConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $rotatedConfig.Agent.Https | Add-Member -NotePropertyName CertificateStoreThumbprint -NotePropertyValue $activeStoreThumbprint -Force
            $rotatedConfig.Agent.Https | Add-Member -NotePropertyName PreviousCertificateSha256Fingerprint -NotePropertyValue $existingCertificateFingerprint -Force
            $rotatedConfig.Agent.Https | Add-Member -NotePropertyName PreviousCertificateAcceptUntilUtc `
                -NotePropertyValue ([DateTimeOffset]::UtcNow.AddDays($CertificateOverlapDays).ToString('O')) -Force
            [IO.File]::WriteAllText($stagingConfigPath, ($rotatedConfig | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
        }
    }
    else {
        $newStoreCertificate = New-AgentStoreCertificate
        $newStoreCertificateCreated = $true
        $grantActiveCertificateKey = $true
        $activeStoreThumbprint = $newStoreCertificate.Thumbprint
        $activeCertificateOwnedByInstaller = $true
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $activeCertificateFingerprint = ([BitConverter]::ToString($sha256.ComputeHash($newStoreCertificate.RawData))).Replace('-', '') }
        finally { $sha256.Dispose() }
        $tokenPepper = New-RandomBase64 48
        $configuration = [ordered]@{
            Agent = [ordered]@{
                AgentId = $AgentId
                ListenUrl = "http://127.0.0.1:$HttpsPort"
                DataDirectory = $data
                MockMode = [bool]$MockMode
                EnablePolling = $true
                EnableSimulator = [bool]$MockMode
                SchedulerTickSeconds = 1
                PairingCodeLifetimeMinutes = 10
                TokenPepper = $tokenPepper
                Retention = [ordered]@{ RawDays = 7; RawMaxMegabytes = 500; EventDays = 90; AuditDays = 180 }
                Https = [ordered]@{
                    Enabled = $true
                    Port = $HttpsPort
                    CertificatePath = (Join-Path $install 'certs\agent.pfx')
                    CertificatePasswordEnvironmentVariable = 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD'
                    CertificateStoreThumbprint = $activeStoreThumbprint
                    PreviousCertificateSha256Fingerprint = $null
                    PreviousCertificateAcceptUntilUtc = $null
                }
                Switches = $switchConfigurations
            }
            Logging = [ordered]@{ LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
            AllowedHosts = '*'
        }
        [IO.File]::WriteAllText((Join-Path $staging 'appsettings.Production.json'),
            ($configuration | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
    }

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
            if ($databaseExistence[$databaseFile]) {
                Copy-Item -LiteralPath $databasePath -Destination (Join-Path $transactionDataBackup $databaseFile) -Force
            }
        }
        $schemaBackupsBefore = @(Get-ChildItem -LiteralPath $data -Filter 'switchwatch.db.schema-*.bak' -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName })
        if ($receiptExistedBefore) { Copy-Item -LiteralPath $receiptPath -Destination $receiptBackupPath -Force }
        $databaseSnapshotTaken = $true
        Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
            -Stage 'data-backed-up' -Status 'running' -Version ([string]$sourceManifest.version)
    }

    if (-not $dataCreated) {
        $dataAclSnapshot = @(Get-SswDirectoryAclSnapshot -Path $data)
    }

    Write-SswStep 'Agent 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
        -Stage 'program-swapped' -Status 'running' -Version ([string]$sourceManifest.version)

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
    & sc.exe description $serviceName 'Samsung IES4224GP read-only monitoring Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 설명 설정에 실패했습니다.' }
    & sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 복구 정책 설정에 실패했습니다.' }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '서비스 SID 활성화에 실패했습니다.' }

    if (-not $Repair) {
        $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Force `
            -Value @('DOTNET_ENVIRONMENT=Production') | Out-Null
    }

    Write-SswStep '서비스 SID 기준 최소 폴더 권한 설정'
    $serviceSid = Get-SswServiceSid -Name $serviceName
    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute
    $dataAclChanged = $true
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify
    if ($grantActiveCertificateKey) {
        Grant-SswCertificatePrivateKeyRead -Certificate $newStoreCertificate -ServiceSid $serviceSid
    }

    if (-not $SkipFirewall) {
        $firewallIsExact = $existingFirewallRule -and
            (Test-SswAgentFirewallRuleExact -Snapshot $existingFirewallRule -Port $HttpsPort -RemoteAddress $ViewerRemoteAddress)
        if (-not $firewallIsExact) {
            Write-SswStep '제품 소유권과 정확한 범위를 가진 인바운드 방화벽 규칙 적용'
            if ($existingFirewallRule) {
                Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction Stop | Remove-NetFirewallRule
            }
            New-SswAgentFirewallRule -Port $HttpsPort -RemoteAddress $ViewerRemoteAddress
            $firewallChanged = $true
        }
    }

    $shouldStart = -not $DoNotStart -and ($MockMode -or ($Repair -and $previousServiceWasRunning))
    if ($shouldStart) {
        Write-SswStep 'Agent 서비스 시작 및 readiness 확인'
        Start-Service -Name $serviceName
        $readinessTimeoutSeconds = if ($MockMode) { 45 } else { 210 }
        $healthStatus = Invoke-SswLocalHealthProbe -Port $HttpsPort -TimeoutSeconds $readinessTimeoutSeconds
        Write-Host "  readiness: $healthStatus"
    }
    elseif (-not $Repair -and -not $MockMode) {
        Write-Host '실환경 Agent는 자격 증명 저장 전까지 시작하지 않습니다.'
    }

    $receipt = [ordered]@{
        receiptVersion = 1
        product = 'SamsungSwitchWatchAgent'
        agentId = $AgentId
        switchId = $SwitchId
        credentialId = $CredentialId
        switchCount = $switchConfigurations.Count
        switchInventoryHash = $switchInventoryHash
        httpsPort = $HttpsPort
        certificateSha256 = $activeCertificateFingerprint
        certificateStoreThumbprint = $activeStoreThumbprint
        certificateOwnedByInstaller = $activeCertificateOwnedByInstaller
        previousCertificateSha256 = if ($RotateCertificate) { $existingCertificateFingerprint } else { $null }
        previousCertificateStoreThumbprint = if ($RotateCertificate) { $existingStoreThumbprint } else { $null }
        previousCertificateOwnedByInstaller = if ($RotateCertificate) { $existingCertificateOwnedByInstaller } else { $false }
        previousCertificateAcceptUntilUtc = if ($RotateCertificate) { [DateTimeOffset]::UtcNow.AddDays($CertificateOverlapDays).ToString('O') } else { $null }
        installedVersion = [string]$sourceManifest.version
        sourceCommit = [string]$sourceManifest.sourceCommit
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($receiptTemporary, $receipt, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        [IO.File]::Replace($receiptTemporary, $receiptPath, $receiptReplaceBackup, $true)
        if (Test-Path -LiteralPath $receiptReplaceBackup -PathType Leaf) { Remove-Item -LiteralPath $receiptReplaceBackup -Force }
    }
    else { Move-Item -LiteralPath $receiptTemporary -Destination $receiptPath }

    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    if (Test-Path -LiteralPath $transactionDataBackup) { Remove-Item -LiteralPath $transactionDataBackup -Recurse -Force }
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
        -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    Write-Host ''
    Write-Host '설치가 완료되었습니다.' -ForegroundColor Green
    if (-not $Repair) {
        Write-Host "인증서 SHA-256 지문: $activeCertificateFingerprint"
        Write-Host "자격 증명 ID: $CredentialId"
        if (-not $MockMode) { Write-Host '다음 단계: 관리자 PowerShell에서 set-switch-credential.ps1을 실행하세요.' }
    }
}
catch {
    $failure = $_
    Write-Warning '설치 실패를 감지해 변경 사항을 복구합니다.'
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
        [pscustomobject]@{ Name = 'remove-new-certificate'; Action = {
            if ($newStoreCertificateCreated -and $newStoreCertificate) {
                $newCertificatePath = "Cert:\LocalMachine\My\$($newStoreCertificate.Thumbprint)"
                if (Test-Path -LiteralPath $newCertificatePath) { Remove-Item -LiteralPath $newCertificatePath -Force }
            }
        } },
        [pscustomobject]@{ Name = 'restore-database'; Action = {
            if ($databaseSnapshotTaken -and -not $dataCreated) {
                foreach ($databaseFile in $databaseFiles) {
                    $databasePath = Join-Path $data $databaseFile
                    if (Test-Path -LiteralPath $databasePath -PathType Leaf) { Remove-Item -LiteralPath $databasePath -Force }
                    if ([bool]$databaseExistence[$databaseFile]) {
                        Copy-Item -LiteralPath (Join-Path $transactionDataBackup $databaseFile) -Destination $databasePath -Force
                    }
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
            if ($firewallChanged) { Restore-SswAgentFirewallSnapshot -Snapshot $existingFirewallRule }
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
            if ($Repair -and $previousServiceWasRunning -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                Start-Service -Name $serviceName
            }
        } }
    ))
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install' -TransactionId $transactionId `
        -Stage 'rollback-completed' -Status 'failed' -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) {
        Write-Warning ("일부 자동 복구 단계가 실패했습니다: {0}" -f ($rollbackErrors -join ', '))
    }
    throw $failure
}

function New-AgentStoreCertificate {
    Write-SswStep 'LocalMachine 인증서 저장소에 비내보내기 HTTPS 인증서 생성'
    $dnsNames = @($env:COMPUTERNAME, 'localhost') | Select-Object -Unique
    $created = New-SelfSignedCertificate -DnsName $dnsNames -CertStoreLocation 'Cert:\LocalMachine\My' `
        -FriendlyName "Samsung Switch Watch Agent $AgentId" -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(3)
    if (-not $created.HasPrivateKey) { throw '새 HTTPS 인증서의 개인 키를 만들지 못했습니다.' }
    return $created
}
finally {
    $pfxPasswordText = $null
    if ($newStoreCertificate) { $newStoreCertificate.Dispose() }
}
