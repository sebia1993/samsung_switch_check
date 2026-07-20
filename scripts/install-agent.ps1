param(
    [string]$SourceDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$ServiceName = 'SamsungSwitchWatchAgent',
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
    [switch]$DoNotStart
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$serviceExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$productionConfig = Join-Path $install 'appsettings.Production.json'
$certificateDirectory = Join-Path $install 'certs'
$certificatePath = Join-Path $certificateDirectory 'agent.pfx'
$firewallRuleName = 'Samsung Switch Watch Agent HTTPS'

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "Agent 배포 파일을 찾지 못했습니다: $sourceExe"
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "서비스가 이미 설치되어 있습니다: $ServiceName"
}
if (-not $MockMode -and $SwitchHost -eq '192.0.2.10') {
    throw '실환경 설치에는 -SwitchHost로 실제 관리 주소를 지정해야 합니다.'
}
if (-not $SkipFirewall -and [string]::IsNullOrWhiteSpace($ViewerRemoteAddress)) {
    throw '방화벽을 열 Viewer PC 주소를 -ViewerRemoteAddress로 지정하거나 -SkipFirewall을 사용하세요.'
}
if (Test-Path -LiteralPath $install) {
    $existing = Get-ChildItem -LiteralPath $install -Force -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) { throw "설치 폴더가 비어 있지 않습니다: $install" }
}

Write-SswStep '설치 폴더와 데이터 폴더 준비'
New-Item -ItemType Directory -Path $install, $data, $certificateDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notin @('data', 'certs') } |
    Copy-Item -Destination $install -Recurse -Force

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

Write-SswStep 'Agent 전용 HTTPS 인증서 생성'
$pfxPasswordText = New-RandomBase64 32
$pfxPassword = ConvertTo-SecureString $pfxPasswordText -AsPlainText -Force
$dnsNames = @($env:COMPUTERNAME, 'localhost') | Select-Object -Unique
$certificate = New-SelfSignedCertificate -DnsName $dnsNames -CertStoreLocation 'Cert:\LocalMachine\My' `
    -FriendlyName 'Samsung Switch Watch Agent' -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3)
try {
    Export-PfxCertificate -Cert $certificate -FilePath $certificatePath -Password $pfxPassword | Out-Null
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
        AllowRemotePairingBootstrap = $false
        SchedulerTickSeconds = 1
        PairingCodeLifetimeMinutes = 10
        TokenPepper = $tokenPepper
        Retention = [ordered]@{ RawDays = 7; RawMaxMegabytes = 500; EventDays = 90; AuditDays = 180 }
        Https = [ordered]@{
            Enabled = $true
            Port = $HttpsPort
            CertificatePath = $certificatePath
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
[IO.File]::WriteAllText($productionConfig, ($configuration | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))

Write-SswStep '저권한 Windows 서비스 계정 권한 설정'
& icacls.exe $install /inheritance:r | Out-Null
& icacls.exe $install /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Agent 설치 폴더 권한 설정에 실패했습니다.' }
& icacls.exe $data /inheritance:r | Out-Null
& icacls.exe $data /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Agent 데이터 폴더 권한 설정에 실패했습니다.' }

Write-SswStep 'Windows 서비스 등록'
& sc.exe create $ServiceName "binPath= `"$serviceExe`"" 'start= auto' 'obj= NT AUTHORITY\LocalService' `
    'DisplayName= Samsung Switch Watch Agent' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows 서비스 등록에 실패했습니다.' }
& sc.exe description $ServiceName 'Samsung IES4224GP read-only monitoring Agent' | Out-Null
& sc.exe failure $ServiceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Force `
    -Value @('DOTNET_ENVIRONMENT=Production', "SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=$pfxPasswordText") | Out-Null

if (-not $SkipFirewall) {
    Write-SswStep 'Viewer PC로 제한한 인바운드 방화벽 규칙 생성'
    New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $HttpsPort -RemoteAddress $ViewerRemoteAddress -Profile Domain,Private | Out-Null
}

if (-not $DoNotStart -and $MockMode) {
    Write-SswStep 'Agent 서비스 시작'
    Start-Service -Name $ServiceName
}
elseif (-not $MockMode) {
    Write-Host '실환경 Agent는 자격 증명 저장 전까지 시작하지 않습니다.'
}

Write-Host ''
Write-Host '설치가 완료되었습니다.' -ForegroundColor Green
Write-Host "인증서 SHA-256 지문: $fingerprint"
Write-Host "자격 증명 ID: $CredentialId"
if (-not $MockMode) {
    Write-Host '다음 단계: 관리자 PowerShell에서 set-switch-credential.ps1을 실행하세요. 완료되면 서비스가 시작됩니다.'
}
Write-Host '지문과 일회용 페어링 코드는 안전한 사내 경로로 Viewer 운영자에게 전달하세요.'
