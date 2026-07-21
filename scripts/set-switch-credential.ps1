param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$CredentialId = 'samsung-switch-readonly',
    [Parameter(Mandatory = $true)][string]$Username
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$exe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
if ([string]::IsNullOrWhiteSpace($CredentialId) -or $CredentialId.Length -gt 64 -or $CredentialId -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'CredentialId는 64자 이하의 영문자, 숫자, 하이픈, 밑줄만 사용할 수 있습니다.'
}
if ([string]::IsNullOrWhiteSpace($Username) -or $Username.Length -gt 128 -or $Username -match '[\r\n\x00]') {
    throw 'Username은 CR, LF, NUL이 없는 128자 이하 문자열이어야 합니다.'
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $exe" }
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw "Agent 서비스를 찾지 못했습니다: $serviceName" }

$configPath = Join-Path $install 'appsettings.Production.json'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$configuredCredentialIds = @($config.Agent.Switches | ForEach-Object { [string]$_.CredentialId })
if ($CredentialId -notin $configuredCredentialIds) {
    throw "설치 설정에 등록되지 않은 CredentialId입니다: $CredentialId"
}
$dataDirectory = [IO.Path]::GetFullPath([string]$config.Agent.DataDirectory)
Assert-SswProductPath -Path $dataDirectory -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
$credentialDirectory = Join-Path $dataDirectory 'credentials'
$credentialPath = Join-Path $credentialDirectory "$CredentialId.bin"
$credentialBackup = Join-Path $credentialDirectory (".$CredentialId.{0}.rollback" -f [Guid]::NewGuid().ToString('N'))
$hadExistingCredential = Test-Path -LiteralPath $credentialPath -PathType Leaf
$credentialWriteAttempted = $false
$credentialUpdateValidated = $false
$rollbackSucceeded = $false

$agentUri = [Uri]([string]$config.Agent.ListenUrl)
if ($agentUri.Scheme -ne 'http' -or $agentUri.Port -lt 1) { throw 'Agent HTTP ListenUrl 설정이 올바르지 않습니다.' }

function Assert-SswCredentialStartFirewall {
    $receiptPath = Join-Path $dataDirectory 'install-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Agent 설치 영수증을 찾지 못했습니다: $receiptPath" }
    try { $currentReceipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Agent 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
    $switches = @($config.Agent.Switches)
    $inventoryHash = Get-SswSwitchInventoryHash -Switches $switches
    $receiptVersion = Assert-SswAgentInstallReceipt -Receipt $currentReceipt -AgentId ([string]$config.Agent.AgentId) `
        -SwitchInventoryHash $inventoryHash -SwitchCount $switches.Count
    $receiptHttpPort = 0
    if (-not [int]::TryParse([string]$currentReceipt.httpPort, [ref]$receiptHttpPort) -or
        $receiptVersion -ne 2 -or $receiptHttpPort -ne $agentUri.Port) {
        throw '현재 HTTP 설정과 일치하는 v2 Agent 설치 영수증이 필요합니다.'
    }
    $allowedAddresses = @(ConvertTo-SswViewerRemoteAddresses -Address @($currentReceipt.viewerRemoteAddresses))
    Assert-SswAgentFirewallGateReady -Port $agentUri.Port -AgentExecutablePath $exe
    $snapshot = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
    if (-not $snapshot -or -not (Test-SswAgentFirewallRuleExact -Snapshot $snapshot -Port $agentUri.Port `
        -RemoteAddress $allowedAddresses)) {
        throw 'Agent 설치 영수증과 정확히 일치하는 폐쇄형 HTTP 방화벽 규칙이 필요합니다.'
    }
}

Assert-SswCredentialStartFirewall
$oldDotnetEnvironment = $env:DOTNET_ENVIRONMENT
$env:DOTNET_ENVIRONMENT = 'Production'

try {
    if ($hadExistingCredential) {
        [IO.File]::Copy($credentialPath, $credentialBackup, $false)
    }
    Push-Location -LiteralPath $install
    try {
        Write-SswStep '비밀번호를 로컬 대화형 입력으로 DPAPI 저장'
        $credentialWriteAttempted = $true
        & $exe credential set $CredentialId $Username
        if ($LASTEXITCODE -ne 0) { throw '스위치 자격 증명 저장에 실패했습니다.' }
    }
    finally { Pop-Location }

    $service = Get-Service -Name $serviceName
    Assert-SswCredentialStartFirewall
    if ($service.Status -eq 'Running') { Restart-Service -Name $serviceName }
    else { Start-Service -Name $serviceName }
    $healthStatus = Invoke-SswLocalHealthProbe -Port $agentUri.Port -TimeoutSeconds 210
    $credentialUpdateValidated = $true
    Write-SswStep "자격 증명 저장 및 Agent 재시작 완료 ($healthStatus)"
}
catch {
    $failure = $_
    if ($credentialWriteAttempted) {
        try {
            Write-Warning '새 자격 증명 검증에 실패해 이전 상태로 복구합니다.' -WarningAction Continue
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            if ($hadExistingCredential) {
                if (Test-Path -LiteralPath $credentialPath -PathType Leaf) {
                    [IO.File]::Replace($credentialBackup, $credentialPath, $null)
                }
                else {
                    Move-Item -LiteralPath $credentialBackup -Destination $credentialPath
                }
                Assert-SswCredentialStartFirewall
                Start-Service -Name $serviceName
                $restoredStatus = Invoke-SswLocalHealthProbe -Port $agentUri.Port -TimeoutSeconds 210
                Write-SswStep "이전 자격 증명 복구 및 Agent 재시작 완료 ($restoredStatus)"
            }
            elseif (Test-Path -LiteralPath $credentialPath -PathType Leaf) {
                [IO.File]::Delete($credentialPath)
            }
            $rollbackSucceeded = $true
        }
        catch {
            throw '새 자격 증명 검증과 이전 자격 증명 자동 복구가 모두 실패했습니다. 기존 백업을 보존했습니다. 진단 코드: CREDENTIAL_ROLLBACK_FAILED'
        }
    }
    throw $failure
}
finally {
    $env:DOTNET_ENVIRONMENT = $oldDotnetEnvironment
    if (($credentialUpdateValidated -or $rollbackSucceeded -or -not $credentialWriteAttempted) -and
        (Test-Path -LiteralPath $credentialBackup -PathType Leaf)) {
        [IO.File]::Delete($credentialBackup)
    }
}
