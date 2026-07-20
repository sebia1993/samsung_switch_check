param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [switch]$RemoveData
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
if ($RemoveData) { Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch' }

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$productionConfig = Join-Path $install 'appsettings.Production.json'
$legacyProductProof = $false
$ownedCertificateThumbprints = New-Object Collections.Generic.List[string]
if ($service -and (Test-Path -LiteralPath $productionConfig -PathType Leaf)) {
    try {
        $installedConfig = Get-Content -LiteralPath $productionConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        $legacyProductProof = -not [string]::IsNullOrWhiteSpace([string]$installedConfig.Agent.AgentId)
    }
    catch { $legacyProductProof = $false }
}
if ($RemoveData) {
    $receiptPath = Join-Path $data 'install-receipt.json'
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        try {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $ownedProperty = $receipt.PSObject.Properties['certificateOwnedByInstaller']
            $thumbprintProperty = $receipt.PSObject.Properties['certificateStoreThumbprint']
            if ($receipt.product -eq 'SamsungSwitchWatchAgent' -and $ownedProperty -and $ownedProperty.Value -eq $true -and
                $thumbprintProperty -and ([string]$thumbprintProperty.Value) -match '^[0-9A-Fa-f]{40}$') {
                $ownedCertificateThumbprints.Add(([string]$thumbprintProperty.Value).ToUpperInvariant())
            }
            $previousOwnedProperty = $receipt.PSObject.Properties['previousCertificateOwnedByInstaller']
            $previousThumbprintProperty = $receipt.PSObject.Properties['previousCertificateStoreThumbprint']
            if ($receipt.product -eq 'SamsungSwitchWatchAgent' -and $previousOwnedProperty -and $previousOwnedProperty.Value -eq $true -and
                $previousThumbprintProperty -and ([string]$previousThumbprintProperty.Value) -match '^[0-9A-Fa-f]{40}$') {
                $ownedCertificateThumbprints.Add(([string]$previousThumbprintProperty.Value).ToUpperInvariant())
            }
        }
        catch { Write-Warning '설치 영수증을 읽지 못해 인증서 저장소 항목은 자동 제거하지 않습니다.' }
    }
}

$transactionId = [Guid]::NewGuid().ToString('N')
$journalPath = Join-Path $env:ProgramData 'SamsungSwitchWatch-Operations\agent-uninstall.json'
Write-SswOperationJournal -Path $journalPath -Operation 'agent-uninstall' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running'

$errors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'stop-service'; Action = {
        $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($current -and $current.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
        }
    } },
    [pscustomobject]@{ Name = 'delete-service'; Action = {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "서비스 제거 요청에 실패했습니다: $serviceName" }
            Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
        }
    } },
    [pscustomobject]@{ Name = 'remove-owned-firewall'; Action = {
        $snapshot = Get-SswAgentFirewallSnapshot
        if ($null -eq $snapshot) { return }
        if (Test-SswOwnedAgentFirewallRule -Snapshot $snapshot) {
            Remove-SswOwnedAgentFirewallRule
            return
        }
        $legacyOwned = $legacyProductProof -and $snapshot.Name -ne 'SamsungSwitchWatchAgent-Https' -and
            [string]::IsNullOrWhiteSpace([string]$snapshot.Group) -and
            [string]::IsNullOrWhiteSpace([string]$snapshot.Description)
        if (-not $legacyOwned) { throw '소유권 표식이 없는 방화벽 규칙은 보존했습니다.' }
        Get-NetFirewallRule -DisplayName 'Samsung Switch Watch Agent HTTPS' -ErrorAction Stop | Remove-NetFirewallRule
    } },
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (Test-Path -LiteralPath $install) {
            Write-SswStep "프로그램 폴더 제거: $install"
            Remove-Item -LiteralPath $install -Recurse -Force
        }
    } },
    [pscustomobject]@{ Name = 'remove-owned-certificate'; Action = {
        if ($RemoveData) {
            foreach ($ownedCertificateThumbprint in @($ownedCertificateThumbprints | Select-Object -Unique)) {
                $certificatePath = "Cert:\LocalMachine\My\$ownedCertificateThumbprint"
                if (Test-Path -LiteralPath $certificatePath) {
                    $certificate = Get-Item -LiteralPath $certificatePath
                    if ($certificate.FriendlyName -notlike 'Samsung Switch Watch Agent *') {
                        throw '설치 영수증과 저장소 소유권 표식이 일치하지 않아 인증서를 보존했습니다.'
                    }
                    Remove-Item -LiteralPath $certificatePath -Force
                }
            }
        }
    } },
    [pscustomobject]@{ Name = 'remove-data'; Action = {
        if ($RemoveData -and (Test-Path -LiteralPath $data)) {
            Write-SswStep "이력·자격 증명 데이터 제거: $data"
            Remove-Item -LiteralPath $data -Recurse -Force
            Write-Warning '데이터 폴더는 복구되지 않습니다.'
        }
        elseif (Test-Path -LiteralPath $data) { Write-Host "데이터는 보존했습니다: $data" }
    } }
))

$status = if ($errors.Count -eq 0) { 'succeeded' } else { 'failed' }
Write-SswOperationJournal -Path $journalPath -Operation 'agent-uninstall' -TransactionId $transactionId `
    -Stage 'completed' -Status $status -ErrorCodes $errors
if ($errors.Count -gt 0) { throw "일부 제거 단계가 실패했습니다: $($errors -join ', ')" }
Write-SswStep 'Agent 제거 완료'
