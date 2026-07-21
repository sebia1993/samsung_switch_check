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
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'

$configuration = $null
$receipt = $null
$legacyOwnedCertificateThumbprints = @()
$configPath = Join-Path $install 'appsettings.Production.json'
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try { $configuration = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Agent 설정을 읽지 못해 안전하게 제거할 수 없습니다: $($_.Exception.Message)" }
    $installedDataDirectory = [IO.Path]::GetFullPath([string]$configuration.Agent.DataDirectory)
    Assert-SswProductPath -Path $installedDataDirectory -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
    if ($PSBoundParameters.ContainsKey('DataDirectory') -and
        -not $data.Equals($installedDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw '-DataDirectory는 설치된 Agent 설정과 정확히 일치해야 합니다.'
    }
    $data = $installedDataDirectory
}
elseif ($RemoveData) {
    throw 'Agent 설정이 없어 삭제 대상 데이터 폴더를 안전하게 확인할 수 없습니다. 프로그램 제거와 데이터 삭제를 분리하세요.'
}

$receiptPath = Join-Path $data 'install-receipt.json'
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    if (-not $configuration) { throw '설치 영수증은 있지만 Agent 설정이 없어 제품 소유권을 안전하게 확인할 수 없습니다.' }
    try { $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Agent 설치 영수증을 읽지 못해 안전하게 제거할 수 없습니다: $($_.Exception.Message)" }
    $configuredSwitches = @($configuration.Agent.Switches)
    $switchInventoryHash = Get-SswSwitchInventoryHash -Switches $configuredSwitches
    $receiptVersion = Assert-SswAgentInstallReceipt -Receipt $receipt -AgentId ([string]$configuration.Agent.AgentId) `
        -SwitchInventoryHash $switchInventoryHash -SwitchCount $configuredSwitches.Count
    if ($receiptVersion -eq 1) {
        $legacyOwnedCertificateThumbprints = @(Get-SswLegacyOwnedAgentCertificateThumbprints `
            -Receipt $receipt -Configuration $configuration)
    }
}
elseif ($RemoveData) {
    throw '유효한 설치 영수증이 없어 Agent 데이터 폴더를 자동 삭제하지 않습니다.'
}

Assert-SswAgentFirewallNameSafety
$legacyCertificateCleanupComplete = $legacyOwnedCertificateThumbprints.Count -eq 0

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
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
        Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing
        Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https' -AllowMissing
    } },
    [pscustomobject]@{ Name = 'remove-owned-legacy-certificates'; Action = {
        foreach ($thumbprint in $legacyOwnedCertificateThumbprints) {
            $certificatePath = "Cert:\LocalMachine\My\$thumbprint"
            if (Test-Path -LiteralPath $certificatePath) {
                Write-SswStep "구 설치기 소유 인증서 제거: $thumbprint"
                Remove-Item -LiteralPath $certificatePath -Force
            }
        }
        $legacyCertificateCleanupComplete = $true
    } },
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (-not $legacyCertificateCleanupComplete) {
            throw '구 설치기 소유 인증서 정리가 실패해 재시도에 필요한 프로그램 설정을 보존합니다.'
        }
        if (Test-Path -LiteralPath $install) {
            Write-SswStep "프로그램 폴더 제거: $install"
            Remove-Item -LiteralPath $install -Recurse -Force
        }
    } },
    [pscustomobject]@{ Name = 'remove-data'; Action = {
        if (-not $legacyCertificateCleanupComplete) {
            throw '구 설치기 소유 인증서 정리가 실패해 재시도에 필요한 설치 영수증을 보존합니다.'
        }
        if ($RemoveData -and (Test-Path -LiteralPath $data)) {
            Write-SswStep "이력·자격 증명 데이터 제거: $data"
            Remove-Item -LiteralPath $data -Recurse -Force
            Write-Warning '데이터 폴더는 복구되지 않습니다.' -WarningAction Continue
        }
        elseif (Test-Path -LiteralPath $data) { Write-Host "데이터는 보존했습니다: $data" }
    } }
))

$status = if ($errors.Count -eq 0) { 'succeeded' } else { 'failed' }
Write-SswOperationJournal -Path $journalPath -Operation 'agent-uninstall' -TransactionId $transactionId `
    -Stage 'completed' -Status $status -ErrorCodes $errors
if ($errors.Count -gt 0) { throw "일부 제거 단계가 실패했습니다: $($errors -join ', ')" }
Write-SswStep 'Agent 제거 완료'
