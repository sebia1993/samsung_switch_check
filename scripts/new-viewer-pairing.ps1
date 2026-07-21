param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$AgentHost,
    [switch]$CopyToClipboard
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

function Resolve-SswPairingHost {
    param([string]$RequestedHost)

    if (-not [string]::IsNullOrWhiteSpace($RequestedHost)) {
        $candidate = $RequestedHost.Trim()
        $parsed = $null
        $isIpAddress = [Net.IPAddress]::TryParse($candidate, [ref]$parsed)
        $isDnsName = $candidate -match '^(?=.{1,253}$)(?![-.])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$'
        if (-not $isIpAddress -and -not $isDnsName) {
            throw 'AgentHost는 유효한 IPv4 주소 또는 DNS 이름이어야 합니다.'
        }
        if ($isIpAddress -and $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
            throw '현재 POC의 자동 연결 문자열은 IPv4 AgentHost만 지원합니다.'
        }
        return $candidate
    }

    $eligibleInterfaceIndexes = $null
    try {
        $activeAdapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' })
        $nonVirtualAdapters = @($activeAdapters | Where-Object {
            $virtualProperty = $_.PSObject.Properties['Virtual']
            $hardwareProperty = $_.PSObject.Properties['HardwareInterface']
            if ($virtualProperty) {
                -not [Convert]::ToBoolean($virtualProperty.Value)
            }
            elseif ($hardwareProperty) {
                [Convert]::ToBoolean($hardwareProperty.Value)
            }
            else {
                $true
            }
        })
        $eligibleAdapters = if ($nonVirtualAdapters.Count -gt 0) { $nonVirtualAdapters } else { $activeAdapters }
        $eligibleInterfaceIndexes = @($eligibleAdapters | Select-Object -ExpandProperty InterfaceIndex -Unique)
    }
    catch {
        # Get-NetAdapter 정보가 없는 환경에서는 Get-NetIPAddress의 활성 주소 판정을 사용합니다.
        $eligibleInterfaceIndexes = $null
    }

    try {
        $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction Stop |
            Where-Object {
                $_.SkipAsSource -eq $false -and
                $_.IPAddress -notmatch '^127\.' -and
                $_.IPAddress -notmatch '^169\.254\.' -and
                $_.IPAddress -ne '0.0.0.0' -and
                ($null -eq $eligibleInterfaceIndexes -or $eligibleInterfaceIndexes.Count -eq 0 -or
                    $eligibleInterfaceIndexes -contains $_.InterfaceIndex)
            } |
            Sort-Object InterfaceAlias, IPAddress |
            Select-Object IPAddress, InterfaceAlias, InterfaceIndex -Unique)
    }
    catch {
        throw '활성 IPv4 주소를 자동으로 찾지 못했습니다. -AgentHost 매개 변수로 Viewer에서 접근 가능한 주소를 지정하세요.'
    }

    if ($addresses.Count -eq 0) {
        throw 'Viewer에서 접근 가능한 활성 IPv4 주소가 없습니다. -AgentHost 매개 변수로 주소를 지정하세요.'
    }
    if ($addresses.Count -eq 1) {
        Write-Host ("  자동 선택: {0} ({1})" -f $addresses[0].IPAddress, $addresses[0].InterfaceAlias)
        return [string]$addresses[0].IPAddress
    }

    Write-Host 'Viewer에서 이 Agent PC로 접근할 때 사용할 IPv4 주소를 선택하세요.' -ForegroundColor Cyan
    for ($index = 0; $index -lt $addresses.Count; $index++) {
        Write-Host ("  [{0}] {1} ({2})" -f ($index + 1), $addresses[$index].IPAddress, $addresses[$index].InterfaceAlias)
    }
    $selectionText = Read-Host '번호'
    $selection = 0
    if (-not [int]::TryParse($selectionText, [ref]$selection) -or
        $selection -lt 1 -or $selection -gt $addresses.Count) {
        throw '올바른 IPv4 주소 번호를 선택하지 않았습니다.'
    }
    return [string]$addresses[$selection - 1].IPAddress
}

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$exe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$receiptPath = Join-Path $data 'install-receipt.json'
$configurationPath = Join-Path $install 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $exe" }
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) { throw "Agent 서비스를 찾지 못했습니다: $serviceName" }
if ($service.Status -ne 'Running') { throw "Agent 서비스가 실행 중이 아닙니다: $serviceName ($($service.Status))" }
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Agent 설치 영수증을 찾지 못했습니다: $receiptPath" }
if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) { throw "Agent 운영 설정을 찾지 못했습니다: $configurationPath" }

try { $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "Agent 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
if ($receipt.receiptVersion -ne 1 -or $receipt.product -ne 'SamsungSwitchWatchAgent') {
    throw 'Agent 설치 영수증의 제품 또는 버전이 올바르지 않습니다.'
}

try { $configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "Agent 운영 설정을 읽지 못했습니다: $($_.Exception.Message)" }
if ($configuration.Agent.Https.Enabled -ne $true) { throw 'Agent 운영 설정에서 HTTPS가 활성화되어 있지 않습니다.' }

$httpsPort = 0
if (-not [int]::TryParse([string]$receipt.httpsPort, [ref]$httpsPort) -or $httpsPort -lt 1 -or $httpsPort -gt 65535) {
    throw 'Agent 설치 영수증의 HTTPS 포트가 올바르지 않습니다.'
}
$configuredHttpsPort = 0
if (-not [int]::TryParse([string]$configuration.Agent.Https.Port, [ref]$configuredHttpsPort) -or
    $configuredHttpsPort -ne $httpsPort) {
    throw 'Agent 설치 영수증과 운영 설정의 HTTPS 포트가 일치하지 않습니다.'
}

$configuredDataDirectory = [string]$configuration.Agent.DataDirectory
if ([string]::IsNullOrWhiteSpace($configuredDataDirectory)) { throw 'Agent 운영 설정의 데이터 폴더가 비어 있습니다.' }
$configuredDataDirectory = if ([IO.Path]::IsPathRooted($configuredDataDirectory)) {
    [IO.Path]::GetFullPath($configuredDataDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $install $configuredDataDirectory))
}
if (-not [string]::Equals($configuredDataDirectory.TrimEnd('\'), $data.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Agent 설치 영수증을 읽은 데이터 폴더와 운영 설정의 데이터 폴더가 일치하지 않습니다.'
}

$fingerprintText = [string]$receipt.certificateSha256
if ($fingerprintText -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'Agent 설치 영수증의 인증서 SHA-256 지문이 정확한 64자리 16진수가 아닙니다.'
}
$fingerprint = $fingerprintText.ToUpperInvariant()

$storeThumbprint = [string]$configuration.Agent.Https.CertificateStoreThumbprint
if ($storeThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw 'Agent 운영 설정의 인증서 저장소 지문이 올바르지 않습니다. Agent 복구 설치를 실행하세요.'
}
$receiptStoreThumbprint = [string]$receipt.certificateStoreThumbprint
if ($receiptStoreThumbprint -notmatch '^[0-9A-Fa-f]{40}$' -or
    -not [string]::Equals($storeThumbprint, $receiptStoreThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Agent 설치 영수증과 운영 설정의 인증서 저장소 지문이 일치하지 않습니다.'
}
$certificatePath = "Cert:\LocalMachine\My\$storeThumbprint"
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw 'Agent HTTPS 인증서를 LocalMachine 인증서 저장소에서 찾지 못했습니다.'
}
$certificate = Get-Item -LiteralPath $certificatePath
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $activeFingerprint = ([BitConverter]::ToString($sha256.ComputeHash($certificate.RawData))).Replace('-', '') }
finally {
    $sha256.Dispose()
    $certificate.Dispose()
}
if (-not [string]::Equals($activeFingerprint, $fingerprint, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Agent 설치 영수증의 인증서 지문이 현재 HTTPS 인증서와 일치하지 않습니다.'
}

try {
    $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $serviceName.Replace("'", "''")) -ErrorAction Stop
    $serviceProcessId = [int]$serviceInfo.ProcessId
    $listener = @(Get-NetTCPConnection -State Listen -LocalPort $httpsPort -ErrorAction Stop |
        Where-Object { $_.OwningProcess -eq $serviceProcessId })
}
catch {
    throw "Agent HTTPS 수신 상태를 확인하지 못했습니다: $($_.Exception.Message)"
}
if ($serviceProcessId -le 0 -or $listener.Count -eq 0) {
    throw "실행 중인 Agent 서비스가 HTTPS 포트 $httpsPort 에서 수신 중이지 않습니다."
}

$selectedAgentHost = Resolve-SswPairingHost -RequestedHost $AgentHost
$agentUri = "https://$selectedAgentHost`:$httpsPort"
$oldDotnetEnvironment = $env:DOTNET_ENVIRONMENT
$pairingData = $null
$pairingJson = $null
$pairingBytes = $null
$bundle = $null
$env:DOTNET_ENVIRONMENT = 'Production'

try {
    Push-Location -LiteralPath $install
    try {
        Write-SswStep 'Viewer용 일회 연결 문자열 생성'
        $output = @(& $exe pairing create --json)
        if ($LASTEXITCODE -ne 0 -or $output.Count -lt 1) { throw '일회용 페어링 코드 생성에 실패했습니다.' }
        try { $pairingData = [string]$output[-1] | ConvertFrom-Json }
        catch { throw 'Agent가 올바른 페어링 JSON을 반환하지 않았습니다.' }
    }
    finally { Pop-Location }

    if ([string]$pairingData.code -notmatch '^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$') {
        throw 'Agent가 올바른 일회용 코드를 반환하지 않았습니다.'
    }
    $expiresUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$pairingData.expiresUtc, [ref]$expiresUtc) -or
        $expiresUtc -le [DateTimeOffset]::UtcNow) {
        throw 'Agent가 올바른 만료 시각을 반환하지 않았습니다.'
    }

    $pairingJson = [ordered]@{
        version = 1
        agentUri = $agentUri
        certificateSha256 = $fingerprint
        code = [string]$pairingData.code
        expiresUtc = $expiresUtc.ToUniversalTime().ToString('O')
    } | ConvertTo-Json -Compress
    $pairingBytes = [Text.Encoding]::UTF8.GetBytes($pairingJson)
    $encoded = [Convert]::ToBase64String($pairingBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $bundle = "SSW1:$encoded"

    $remaining = $expiresUtc - [DateTimeOffset]::UtcNow
    $remainingSeconds = [Math]::Max(0, [Math]::Floor($remaining.TotalSeconds))
    $remainingMinutes = [Math]::Floor($remainingSeconds / 60)
    $remainingRemainderSeconds = $remainingSeconds % 60
    $localExpiryText = $expiresUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')

    Write-Host ''
    Write-Host '아래 연결 문자열 전체를 Viewer의 Agent 연결 창에 붙여 넣으세요.' -ForegroundColor Green
    Write-Host $bundle
    Write-Host ''
    Write-Warning ("이 문자열은 {0}까지 한 번만 사용할 수 있습니다. 남은 시간은 약 {1}분 {2}초입니다. 메신저나 파일에 저장하지 마세요." -f
        $localExpiryText, $remainingMinutes, $remainingRemainderSeconds)
    if ($CopyToClipboard) {
        Set-Clipboard -Value $bundle
        Write-Host '요청에 따라 Windows 클립보드에도 복사했습니다.' -ForegroundColor Yellow
    }
}
finally {
    $env:DOTNET_ENVIRONMENT = $oldDotnetEnvironment
    if ($pairingBytes) { [Array]::Clear($pairingBytes, 0, $pairingBytes.Length) }
    $pairingData = $null
    $pairingJson = $null
    $bundle = $null
    $certificate = $null
}
