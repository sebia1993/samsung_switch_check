param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$AgentId = 'agent-poc-01',
    [string]$SwitchId = 'ACCESS-SW-01',
    [string]$SwitchDisplayName = 'Samsung access switch',
    [string]$SwitchHost = '192.0.2.10',
    [ValidateRange(1, 65535)][int]$SwitchPort = 23,
    [string]$CredentialId = 'samsung-switch-readonly',
    [string]$UplinkPort = '24',
    [ValidateRange(1, 65535)][int]$HttpsPort = 18443,
    [string]$ViewerRemoteAddress,
    [switch]$MockMode,
    [switch]$SkipFirewall,
    [switch]$DoNotStart,
    [switch]$Preflight,
    [switch]$Repair
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$firewallRuleName = 'Samsung Switch Watch Agent HTTPS'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$serviceExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$productionConfig = Join-Path $install 'appsettings.Production.json'
$certificatePath = Join-Path $install 'certs\agent.pfx'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingFirewallRule = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue

Write-SswStep '설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Agent는 Windows x64에서만 설치할 수 있습니다.' }
if (-not (Test-SswAdministrator)) { throw '관리자 권한 PowerShell에서 실행해야 합니다.' }
if ([string]::IsNullOrWhiteSpace($AgentId) -or $AgentId.Length -gt 64 -or $AgentId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'AgentId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
if ([string]::IsNullOrWhiteSpace($SwitchId) -or $SwitchId.Length -gt 64 -or $SwitchId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'SwitchId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
if ([string]::IsNullOrWhiteSpace($CredentialId) -or $CredentialId.Length -gt 64 -or $CredentialId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'CredentialId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
if ([string]::IsNullOrWhiteSpace($SwitchDisplayName) -or $SwitchDisplayName.Length -gt 128 -or $SwitchDisplayName -match '[\x00-\x1F\x7F]') {
    throw 'SwitchDisplayName은 제어문자가 없는 128자 이하 문자열이어야 합니다.'
}
$switchAddress = $null
if (-not [Net.IPAddress]::TryParse($SwitchHost, [ref]$switchAddress) -or
    $switchAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'SwitchHost는 유효한 IPv4 주소여야 합니다.'
}
if ([string]::IsNullOrWhiteSpace($UplinkPort) -or $UplinkPort.Length -gt 32 -or $UplinkPort -notmatch '^[A-Za-z0-9._/-]+$') {
    throw 'UplinkPort는 32자 이하의 안전한 포트 식별자여야 합니다.'
}
if (-not $SkipFirewall -and -not [string]::IsNullOrWhiteSpace($ViewerRemoteAddress)) {
    $viewerAddress = $null
    if (-not [Net.IPAddress]::TryParse($ViewerRemoteAddress, [ref]$viewerAddress) -or
        $viewerAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'ViewerRemoteAddress는 방화벽에서 허용할 단일 IPv4 주소여야 합니다.'
    }
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Agent 배포 파일을 찾지 못했습니다: $sourceExe" }
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
if ($Repair -and -not $existingService) { throw "복구할 서비스가 없습니다: $serviceName" }
if (-not $Repair -and $existingService) { throw "서비스가 이미 설치되어 있습니다. 복구 설치에는 -Repair를 사용하세요: $serviceName" }
if (-not $Repair -and (Test-Path -LiteralPath $install)) {
    throw "설치 폴더가 이미 있습니다. 흔적을 확인하거나 제거 후 다시 시도하세요: $install"
}
if (-not $MockMode -and -not $Repair -and $SwitchHost -eq '192.0.2.10') {
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
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) { throw "설치 인증서를 찾지 못했습니다: $certificatePath" }
    try { $installedConfig = Get-Content -LiteralPath $productionConfig -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "설치 설정을 읽지 못했습니다: $($_.Exception.Message)" }
    $HttpsPort = [int]$installedConfig.Agent.Https.Port
    $serviceProperties = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" `
        -Name Environment -ErrorAction SilentlyContinue
    $installedEnvironment = if ($serviceProperties) { $serviceProperties.Environment } else { @() }
    if (-not ($installedEnvironment | Where-Object { $_ -like 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' })) {
        throw '기존 서비스에 HTTPS 인증서 암호 환경 변수가 없습니다. 자동 복구 설치를 중단합니다.'
    }
}
elseif (-not (Test-SswTcpPortAvailable -Port $HttpsPort)) {
    throw "Agent HTTPS 포트를 이미 다른 프로세스가 사용하고 있습니다: $HttpsPort"
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
$serviceCreated = $false
$firewallCreated = $false
$dataCreated = $false
$installSwapped = $false
$previousServiceWasRunning = $existingService -and $existingService.Status -eq 'Running'
$pfxPasswordText = $null
$dataAclSnapshot = @()
$dataAclChanged = $false

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
        Copy-Item -LiteralPath $productionConfig -Destination (Join-Path $staging 'appsettings.Production.json') -Force
        Copy-Item -LiteralPath (Join-Path $install 'certs') -Destination $staging -Recurse -Force
    }
    else {
        $stagingCertificateDirectory = Join-Path $staging 'certs'
        $stagingCertificatePath = Join-Path $stagingCertificateDirectory 'agent.pfx'
        New-Item -ItemType Directory -Path $stagingCertificateDirectory -Force | Out-Null

        Write-SswStep 'Agent 전용 HTTPS 인증서 생성'
        $pfxPasswordText = New-RandomBase64 32
        $pfxPassword = ConvertTo-SecureString $pfxPasswordText -AsPlainText -Force
        $dnsNames = @($env:COMPUTERNAME, 'localhost') | Select-Object -Unique
        $certificate = New-SelfSignedCertificate -DnsName $dnsNames -CertStoreLocation 'Cert:\LocalMachine\My' `
            -FriendlyName 'Samsung Switch Watch Agent' -KeyAlgorithm RSA -KeyLength 3072 `
            -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3)
        try {
            Export-PfxCertificate -Cert $certificate -FilePath $stagingCertificatePath -Password $pfxPassword | Out-Null
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $fingerprint = ([BitConverter]::ToString($sha256.ComputeHash($certificate.RawData))).Replace('-', '') }
            finally { $sha256.Dispose() }
        }
        finally {
            Remove-Item -LiteralPath ("Cert:\LocalMachine\My\{0}" -f $certificate.Thumbprint) -Force
        }

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
                }
                Switches = @([ordered]@{
                    Id = $SwitchId
                    DisplayName = $SwitchDisplayName
                    Model = 'IES4224GP'
                    Host = $SwitchHost
                    Port = $SwitchPort
                    CredentialId = $CredentialId
                    UplinkPort = $UplinkPort
                })
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

    if (-not $dataCreated) {
        $dataAclSnapshot = @(Get-SswDirectoryAclSnapshot -Path $data)
    }

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
    & sc.exe description $serviceName 'Samsung IES4224GP read-only monitoring Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 설명 설정에 실패했습니다.' }
    & sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 복구 정책 설정에 실패했습니다.' }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '서비스 SID 활성화에 실패했습니다.' }

    if (-not $Repair) {
        $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Force `
            -Value @('DOTNET_ENVIRONMENT=Production', "SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=$pfxPasswordText") | Out-Null
    }

    Write-SswStep '서비스 SID 기준 최소 폴더 권한 설정'
    $serviceSid = Get-SswServiceSid -Name $serviceName
    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute
    $dataAclChanged = $true
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify

    if (-not $SkipFirewall -and -not $existingFirewallRule) {
        Write-SswStep 'Viewer PC로 제한한 인바운드 방화벽 규칙 생성'
        New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP `
            -LocalPort $HttpsPort -RemoteAddress $ViewerRemoteAddress -Profile Domain,Private | Out-Null
        $firewallCreated = $true
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

    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    Write-Host ''
    Write-Host '설치가 완료되었습니다.' -ForegroundColor Green
    if (-not $Repair) {
        Write-Host "인증서 SHA-256 지문: $fingerprint"
        Write-Host "자격 증명 ID: $CredentialId"
        if (-not $MockMode) { Write-Host '다음 단계: 관리자 PowerShell에서 set-switch-credential.ps1을 실행하세요.' }
    }
}
catch {
    $failure = $_
    Write-Warning '설치 실패를 감지해 변경 사항을 복구합니다.'
    try {
        $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($currentService -and $currentService.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
        if ($serviceCreated) {
            & sc.exe delete $serviceName | Out-Null
            Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
        }
        if ($firewallCreated) {
            Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        }
        if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        if ($dataCreated -and (Test-Path -LiteralPath $data)) {
            Remove-Item -LiteralPath $data -Recurse -Force
        }
        elseif ($dataAclChanged -and $dataAclSnapshot.Count -gt 0) {
            Restore-SswDirectoryAclSnapshot -Snapshot $dataAclSnapshot
        }
        if ($Repair -and $previousServiceWasRunning -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Warning "자동 복구 중 추가 오류가 발생했습니다: $($_.Exception.Message)"
    }
    throw $failure
}
finally {
    $pfxPasswordText = $null
}
