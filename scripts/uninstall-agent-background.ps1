param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:LOCALAPPDATA\SamsungSwitchWatch\AgentData",
    [switch]$RemoveData
)

. (Join-Path $PSScriptRoot 'common.ps1')

$taskName = Get-SswAgentBackgroundTaskName
$taskDescription = 'Owned by SamsungSwitchWatch current-user background installer v1'
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$receiptPath = Join-Path $data 'background-install-receipt.json'
$installedExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$runnerPath = Join-Path $install 'run-agent-background.ps1'
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$ownerSid = Get-SswCurrentUserSid

function Get-SswTaskActionArguments {
    return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runnerPath`" -InstallDirectory `"$install`""
}

function Test-SswOwnedBackgroundTask {
    param([AllowNull()][object]$Task)

    if (-not $Task -or [string]$Task.TaskName -ne $taskName -or [string]$Task.TaskPath -ne '\' -or
        [string]$Task.Description -ne $taskDescription) { return $false }
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { return $false }
    try { $taskOwnerSid = ConvertTo-SswIdentitySid -Identity ([string]$Task.Principal.UserId) }
    catch { return $false }
    return $taskOwnerSid -eq $ownerSid -and
        ([string]$Task.Principal.RunLevel -in @('Limited', 'LeastPrivilege')) -and
        ([string]$actions[0].Execute).Equals($powershellPath, [StringComparison]::OrdinalIgnoreCase) -and
        ([string]$actions[0].Arguments).Equals((Get-SswTaskActionArguments), [StringComparison]::Ordinal) -and
        ([string]$actions[0].WorkingDirectory).TrimEnd('\').Equals(
            $install.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Get-SswProcessOwnerSid {
    param([Parameter(Mandatory = $true)][object]$Process)

    try {
        $owner = Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop
        return [string]$owner.Sid
    }
    catch { return $null }
}

function Stop-SswOwnedBackgroundProcesses {
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SamsungSwitchWatch.Agent.exe'" -ErrorAction SilentlyContinue)) {
        if ((Get-SswProcessOwnerSid -Process $process) -ne $ownerSid) { continue }
        $path = [string]$process.ExecutablePath
        $isAgent = $path -and $path.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)
        if (-not $isAgent) { continue }

        $processId = [int]$process.ProcessId
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
        if (-not $current -or (Get-SswProcessOwnerSid -Process $current) -ne $ownerSid) { continue }
        $currentPath = [string]$current.ExecutablePath
        $pathIsOwned = $currentPath -and $currentPath.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)
        if (-not $pathIsOwned) { throw "PID 재사용으로 프로세스 소유권 검증에 실패했습니다: $processId" }
        Stop-Process -Id $processId -Force -ErrorAction Stop
    }
}

function Wait-SswBackgroundTaskStopped {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        if (-not $task -or [string]$task.State -ne 'Running') { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "숨김 Agent 예약 작업이 제한 시간 안에 중지되지 않았습니다: $taskName"
}

if ($env:OS -ne 'Windows_NT') { throw '현재 사용자 숨김 Agent는 Windows에서만 제거할 수 있습니다.' }
if (Test-SswAdministrator) {
    throw '현재 사용자 숨김 Agent 제거는 설치한 계정의 일반 PowerShell에서 실행하세요.'
}
Import-Module ScheduledTasks -ErrorAction Stop
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'SamsungSwitchWatch\AgentData'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "현재 사용자 Agent 설치 영수증을 찾지 못해 자동 제거하지 않습니다: $receiptPath"
}
try { $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "현재 사용자 Agent 설치 영수증을 읽지 못했습니다: $($_.Exception.Message)" }
$null = Assert-SswBackgroundAgentReceipt -Receipt $receipt -InstallDirectory $install `
    -DataDirectory $data -OwnerSid $ownerSid
$task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if (-not $task -or -not (Test-SswOwnedBackgroundTask -Task $task)) {
    throw "제품 소유권이 확인되는 현재 사용자 예약 작업을 찾지 못해 자동 제거하지 않습니다: $taskName"
}
if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
    $actualExeHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualExeHash -ne ([string]$receipt.executableSha256).ToLowerInvariant()) {
        throw '설치된 Agent 실행 파일이 설치 영수증과 달라 자동 제거하지 않습니다.'
    }
}

$transactionId = [Guid]::NewGuid().ToString('N')
$operationRoot = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch\Operations'
Assert-SswProductPath -Path $operationRoot -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'SamsungSwitchWatch\Operations'
$journalPath = Join-Path $operationRoot 'agent-background-uninstall.json'
Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-uninstall' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$receipt.installedVersion)

Write-SswStep '숨김 Agent 예약 작업 중지 및 소유 프로세스 정리'
$taskWasRunning = [string]$task.State -eq 'Running'
$shutdownErrors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'stop-task'; Action = {
        Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        Wait-SswBackgroundTaskStopped
    } },
    [pscustomobject]@{ Name = 'stop-owned-processes'; Action = { Stop-SswOwnedBackgroundProcesses } }
))
if ($shutdownErrors.Count -gt 0) {
    if ($taskWasRunning) { Start-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue }
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-uninstall' -TransactionId $transactionId `
        -Stage 'task-stop-failed' -Status 'failed' -Version ([string]$receipt.installedVersion) -ErrorCodes $shutdownErrors
    throw "예약 작업 또는 소유 프로세스를 안전하게 중지하지 못했습니다: $($shutdownErrors -join ', ')"
}

try {
    $current = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    if (-not $current -or -not (Test-SswOwnedBackgroundTask -Task $current)) {
        throw '제거 직전 예약 작업 소유권 재검증에 실패했습니다.'
    }
    Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
}
catch {
    if ($taskWasRunning) { Start-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue }
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-uninstall' -TransactionId $transactionId `
        -Stage 'task-removal-failed' -Status 'failed' -Version ([string]$receipt.installedVersion) `
        -ErrorCodes @('TASK_UNREGISTER_FAILED')
    throw
}

Write-SswStep '현재 사용자 Agent 프로그램 제거'
$fileErrors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
    } },
    [pscustomobject]@{ Name = 'remove-data'; Action = {
        if ($RemoveData -and (Test-Path -LiteralPath $data)) {
            Remove-Item -LiteralPath $data -Recurse -Force
            Write-Warning 'Agent DB, 설정과 스위치 자격 증명을 제거했으며 복구되지 않습니다.' -WarningAction Continue
        }
    } }
))
$status = if ($fileErrors.Count -eq 0) { 'succeeded' } else { 'failed' }
Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-uninstall' -TransactionId $transactionId `
    -Stage 'completed' -Status $status -Version ([string]$receipt.installedVersion) -ErrorCodes $fileErrors
if ($fileErrors.Count -gt 0) { throw "일부 현재 사용자 Agent 제거 단계가 실패했습니다: $($fileErrors -join ', ')" }

Write-SswStep '현재 사용자 숨김 Agent 제거 완료'
if (-not $RemoveData) {
    Write-Host "DB, 설정과 자격 증명은 다음 설치에 재사용할 수 있도록 보존했습니다: $data"
}
