param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [Parameter(Mandatory = $true)][ValidateCount(1, 32)][string[]]$ViewerRemoteAddress,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'

$configPath = Join-Path $install 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Agent 설정을 찾지 못했습니다: $configPath" }
try { $configuration = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "Agent 설정을 읽지 못했습니다: $($_.Exception.Message)" }
$installedDataDirectory = [IO.Path]::GetFullPath([string]$configuration.Agent.DataDirectory)
Assert-SswProductPath -Path $installedDataDirectory -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
if ($PSBoundParameters.ContainsKey('DataDirectory') -and
    -not $data.Equals($installedDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw '-DataDirectory는 설치된 Agent 설정과 정확히 일치해야 합니다.'
}
$data = $installedDataDirectory
$receiptPath = Join-Path $data 'install-receipt.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Agent 설치 영수증을 찾지 못했습니다: $receiptPath" }
try { $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "Agent 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
$configuredSwitches = @($configuration.Agent.Switches)
$switchInventoryHash = Get-SswSwitchInventoryHash -Switches $configuredSwitches
$receiptVersion = Assert-SswAgentInstallReceipt -Receipt $receipt -AgentId ([string]$configuration.Agent.AgentId) `
    -SwitchInventoryHash $switchInventoryHash -SwitchCount $configuredSwitches.Count
if ($receiptVersion -ne 2) {
    throw 'v0.6 이상의 HTTP 설치 영수증만 갱신할 수 있습니다.'
}
$httpPort = 0
if (-not [int]::TryParse([string]$receipt.httpPort, [ref]$httpPort) -or $httpPort -lt 1 -or $httpPort -gt 65535) {
    throw 'Agent 설치 영수증의 HTTP 포트가 올바르지 않습니다.'
}
$listenUri = $null
if (-not [Uri]::TryCreate([string]$configuration.Agent.ListenUrl, [UriKind]::Absolute, [ref]$listenUri) -or
    $listenUri.Scheme -ne 'http' -or $listenUri.Port -ne $httpPort) {
    throw '설치 영수증과 Agent HTTP 설정의 포트가 일치하지 않습니다.'
}
$validatedAddresses = @(ConvertTo-SswViewerRemoteAddresses -Address $ViewerRemoteAddress)
$agentExecutable = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
if (-not (Test-Path -LiteralPath $agentExecutable -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $agentExecutable" }
Assert-SswAgentFirewallGateReady -Port $httpPort -AgentExecutablePath $agentExecutable
$snapshot = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
if (-not $snapshot) { throw 'Agent HTTP 방화벽 규칙을 찾지 못했습니다.' }
if (-not (Test-SswOwnedAgentFirewallRule -Snapshot $snapshot)) { throw '제품 소유권 표식이 없는 방화벽 규칙은 변경하지 않습니다.' }

Write-Host "  HTTP   : TCP/$httpPort"
Write-Host "  Viewer : $($validatedAddresses -join ', ')"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 방화벽과 영수증은 변경되지 않았습니다.'
    return
}

$transactionId = [Guid]::NewGuid().ToString('N')
$temporaryReceipt = "$receiptPath.$transactionId.tmp"
$replaceBackup = "$receiptPath.$transactionId.bak"
$firewallChanged = $false
$receiptChanged = $false
$preserveRollbackArtifacts = $false
try {
    Write-SswStep 'Agent HTTP 방화벽 허용 Viewer 주소 갱신'
    $firewallChanged = $true
    Get-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Http' -ErrorAction Stop |
        Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $validatedAddresses | Out-Null
    Assert-SswAgentFirewallGateReady -Port $httpPort -AgentExecutablePath $agentExecutable
    $updated = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
    if (-not (Test-SswAgentFirewallRuleExact -Snapshot $updated -Port $httpPort -RemoteAddress $validatedAddresses)) {
        throw '갱신된 방화벽 범위의 검증에 실패했습니다.'
    }

    $receipt | Add-Member -NotePropertyName viewerRemoteAddresses -NotePropertyValue $validatedAddresses -Force
    $receipt | Add-Member -NotePropertyName updatedUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
    [IO.File]::WriteAllText($temporaryReceipt, ($receipt | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($temporaryReceipt, $receiptPath, $replaceBackup, $true)
    $receiptChanged = $true
    Write-SswStep 'Viewer 허용 주소와 설치 영수증 갱신 완료'
}
catch {
    $failure = $_
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'restore-viewer-access-receipt'; Action = {
            if ($receiptChanged) {
                if (-not (Test-Path -LiteralPath $replaceBackup -PathType Leaf)) {
                    throw '설치 영수증 rollback 백업을 찾지 못했습니다.'
                }
                [IO.File]::Replace($replaceBackup, $receiptPath, $null, $true)
            }
        } },
        [pscustomobject]@{ Name = 'restore-viewer-access-firewall'; Action = {
            if ($firewallChanged) { Restore-SswAgentFirewallSnapshot -Snapshot $snapshot }
        } }
    ))
    if ($rollbackErrors.Count -gt 0) {
        $preserveRollbackArtifacts = $true
        throw ("Viewer 주소 갱신 실패 후 자동 복구도 일부 실패했습니다. 원인: {0}; 복구 코드: {1}; 보존 경로: {2}" -f
            $failure.Exception.Message, ($rollbackErrors -join ', '), $replaceBackup)
    }
    throw $failure
}
finally {
    if (-not $preserveRollbackArtifacts) {
        foreach ($artifact in @($temporaryReceipt, $replaceBackup)) {
            if (Test-Path -LiteralPath $artifact -PathType Leaf) { Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue }
        }
    }
}
