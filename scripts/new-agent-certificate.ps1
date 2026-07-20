param(
    [string]$AgentId = 'agent-poc-01',
    [ValidateRange(1, 5)][int]$ValidYears = 3
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

if ([string]::IsNullOrWhiteSpace($AgentId) -or $AgentId.Length -gt 64 -or $AgentId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'AgentId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
$serviceName = Get-SswAgentServiceName
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    throw '인증서 회전 준비는 설치된 Agent 서비스가 필요합니다.'
}

$certificate = $null
try {
    Write-SswStep '회전용 LocalMachine 비내보내기 인증서 생성'
    $dnsNames = @($env:COMPUTERNAME, 'localhost') | Select-Object -Unique
    $certificate = New-SelfSignedCertificate -DnsName $dnsNames -CertStoreLocation 'Cert:\LocalMachine\My' `
        -FriendlyName "Samsung Switch Watch Agent $AgentId rotation" -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears($ValidYears)
    $serviceSid = Get-SswServiceSid -Name $serviceName
    Grant-SswCertificatePrivateKeyRead -Certificate $certificate -ServiceSid $serviceSid
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($certificate.RawData))).Replace('-', '') }
    finally { $sha.Dispose() }

    Write-Host ''
    Write-Host '회전용 인증서를 준비했습니다.' -ForegroundColor Green
    Write-Host "저장소 Thumbprint: $($certificate.Thumbprint)"
    Write-Host "Viewer용 SHA-256 지문: $fingerprint"
    Write-Host '먼저 모든 Viewer에 현재 지문과 새 SHA-256 지문을 함께 등록하세요.'
    Write-Host "그 다음 install-agent.ps1 -Repair -RotateCertificate -RotationCertificateThumbprint $($certificate.Thumbprint) 를 실행하세요."
}
catch {
    if ($certificate) {
        $certificatePath = "Cert:\LocalMachine\My\$($certificate.Thumbprint)"
        if (Test-Path -LiteralPath $certificatePath) { Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue }
    }
    throw
}
finally {
    if ($certificate) { $certificate.Dispose() }
}
