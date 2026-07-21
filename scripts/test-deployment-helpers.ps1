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

    Write-SswStep 'Agent 패키지·데이터·방화벽·롤백 계약 확인'
    $commonText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8
    Assert-DeploymentTest -Condition ($commonText.Contains('$rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint')) `
        -Message 'ACL helper가 대상 루트 junction/symlink를 거부하지 않습니다.'
    foreach ($requiredText in @(
        '[switch]$ReuseData', 'install-receipt.json', 'switchwatch.db-wal', 'switchwatch.db-shm',
        'restore-database', 'Restore-SswAgentFirewallSnapshot', 'Test-SswOwnedAgentFirewallRule',
        'BUILD-MANIFEST.json', 'Get-FileHash', 'CertificateStoreThumbprint', '-KeyExportPolicy NonExportable',
        '[switch]$RotateCertificate', 'PreviousCertificateSha256Fingerprint', 'CertificateOverlapDays',
        '[string]$SwitchesJsonPath', "[ValidateSet('IES4224GP', 'IES4028XP', 'IES4226XP')]", 'switchInventoryHash'
    )) {
        Assert-DeploymentTest -Condition $agentInstall.Contains($requiredText) -Message "Agent 설치 계약 누락: $requiredText"
    }
    Assert-DeploymentTest -Condition ($agentInstall.IndexOf("Name = 'restore-database'") -lt $agentInstall.IndexOf("Name = 'restore-program'")) `
        -Message 'Agent rollback에서 DB가 프로그램보다 먼저 복원되지 않습니다.'
    Assert-DeploymentTest -Condition ($agentInstall.IndexOf('Invoke-SswLocalHealthProbe') -lt $agentInstall.IndexOf("Stage 'completed'")) `
        -Message 'readiness 확인 전에 설치 성공을 기록합니다.'
    Assert-DeploymentTest -Condition ($agentUninstall.Contains('Invoke-SswBestEffortPlan') -and $agentUninstall.Contains('Remove-SswOwnedAgentFirewallRule')) `
        -Message 'Agent 제거가 단계별 계속 실행/소유 방화벽 계약을 사용하지 않습니다.'

    $pairViewer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'pair-viewer.ps1') -Raw -Encoding UTF8
    Assert-DeploymentTest -Condition ($pairViewer.Contains('[ValidateCount(1, 2)]') -and $pairViewer.Contains('SswDualPinnedHttpClientHandler')) `
        -Message 'Viewer 페어링 도우미가 인증서 회전용 dual pin을 지원하지 않습니다.'

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
