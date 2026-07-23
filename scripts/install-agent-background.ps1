param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:LOCALAPPDATA\SamsungSwitchWatch\AgentData",
    [string[]]$AllowedTargetCidrs,
    [switch]$Repair,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')

$taskName = Get-SswAgentBackgroundTaskName
$taskDescription = 'Owned by SamsungSwitchWatch current-user background installer v1'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$sourceBaseConfigPath = Join-Path $source 'appsettings.json'
$sourceProductionConfigPath = Join-Path $source 'appsettings.Production.json'
$installedProductionConfigPath = Join-Path $install 'appsettings.Production.json'
$preservedConfigPath = Join-Path $data 'background-appsettings.Production.json'
$receiptPath = Join-Path $data 'background-install-receipt.json'
$runnerPath = Join-Path $install 'run-agent-background.ps1'
$installedExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$ownerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$ownerSid = Get-SswCurrentUserSid
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Read-SswJsonFile {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)

    try { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "$Label JSON을 읽지 못했습니다: $($_.Exception.Message)" }
}

function Get-SswAgentProperty {
    param([AllowNull()][object]$Configuration, [Parameter(Mandatory = $true)][string]$Name)

    if (-not $Configuration -or -not $Configuration.PSObject.Properties['Agent'] -or
        -not $Configuration.Agent.PSObject.Properties[$Name]) { return $null }
    return $Configuration.Agent.PSObject.Properties[$Name].Value
}

function New-SswBackgroundProductionConfiguration {
    param([AllowNull()][object]$ExistingConfiguration)

    $agentId = if ($ExistingConfiguration -and $ExistingConfiguration.Agent.AgentId) {
        [string]$ExistingConfiguration.Agent.AgentId
    } else { "agent-$env:COMPUTERNAME" }
    $preservedCidrs = if ($ExistingConfiguration -and
        $ExistingConfiguration.Agent.PSObject.Properties['AllowedTargetCidrs']) {
        @($ExistingConfiguration.Agent.AllowedTargetCidrs)
    } else { @() }
    $resolvedCidrs = if ($AllowedTargetCidrs -and @($AllowedTargetCidrs).Count -gt 0) {
        @(ConvertTo-SswIpv4Cidrs -Cidr @($AllowedTargetCidrs))
    } elseif ($preservedCidrs.Count -gt 0) {
        @(ConvertTo-SswIpv4Cidrs -Cidr $preservedCidrs)
    } else {
        $answer = Read-Host 'Switch target CIDR(s), comma separated (example 10.40.0.0/16)'
        $entries = @($answer.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        @(ConvertTo-SswIpv4Cidrs -Cidr $entries)
    }
    return [ordered]@{
        Agent = [ordered]@{
            AgentId = $agentId
            ListenUrl = 'https://0.0.0.0:18443'
            DataDirectory = $data
            MockMode = $false
            AllowedTargetCidrs = @($resolvedCidrs)
            MaxConcurrentExecutions = 2
            RateLimitPerMinute = 60
            MaxRequestBodyBytes = 32768
            MaxCommandsPerRequest = 8
            MaxCommandLength = 128
            MaxOutputBytes = 65536
            Telnet = [ordered]@{
                MaxSessionSeconds = 240
                ImmediateSessionCloseRetryCount = 1
                ImmediateSessionCloseRetryDelaySeconds = 2
            }
        }
        Logging = [ordered]@{
            LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' }
        }
        AllowedHosts = '*'
    }
}

function Get-SswTaskActionArguments {
    param([Parameter(Mandatory = $true)][string]$InstalledRunnerPath)

    return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$InstalledRunnerPath`" -InstallDirectory `"$install`""
}

function Test-SswOwnedBackgroundTask {
    param([AllowNull()][object]$Task)

    if (-not $Task -or [string]$Task.TaskName -ne $taskName -or [string]$Task.TaskPath -ne '\' -or
        [string]$Task.Description -ne $taskDescription) { return $false }
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { return $false }
    try { $taskOwnerSid = ConvertTo-SswIdentitySid -Identity ([string]$Task.Principal.UserId) }
    catch { return $false }
    $expectedArguments = Get-SswTaskActionArguments -InstalledRunnerPath $runnerPath
    return $taskOwnerSid -eq $ownerSid -and
        ([string]$Task.Principal.RunLevel -in @('Limited', 'LeastPrivilege')) -and
        ([string]$actions[0].Execute).Equals($powershellPath, [StringComparison]::OrdinalIgnoreCase) -and
        ([string]$actions[0].Arguments).Equals($expectedArguments, [StringComparison]::Ordinal) -and
        ([string]$actions[0].WorkingDirectory).TrimEnd('\').Equals(
            $install.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Test-SswBackgroundTaskRegistrationMarker {
    param([AllowNull()][object]$Task)

    if (-not $Task -or [string]$Task.TaskName -ne $taskName -or [string]$Task.TaskPath -ne '\' -or
        [string]$Task.Description -ne $taskDescription) { return $false }
    try { return (ConvertTo-SswIdentitySid -Identity ([string]$Task.Principal.UserId)) -eq $ownerSid }
    catch { return $false }
}

function Get-SswProcessOwnerSid {
    param([Parameter(Mandatory = $true)][object]$Process)

    try {
        $owner = Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop
        return [string]$owner.Sid
    }
    catch { return $null }
}

function Get-SswAgentProcesses {
    $result = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SamsungSwitchWatch.Agent.exe'" -ErrorAction SilentlyContinue)) {
        $result += [pscustomobject]@{
            ProcessId = [int]$process.ProcessId
            ExecutablePath = [string]$process.ExecutablePath
            OwnerSid = Get-SswProcessOwnerSid -Process $process
        }
    }
    return $result
}

function Stop-SswOwnedBackgroundProcesses {
    $targets = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SamsungSwitchWatch.Agent.exe'" -ErrorAction SilentlyContinue)) {
        $processOwnerSid = Get-SswProcessOwnerSid -Process $process
        if ($processOwnerSid -ne $ownerSid) { continue }
        $path = [string]$process.ExecutablePath
        $isAgent = $path -and $path.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)
        if ($isAgent) { $targets += [pscustomobject]@{ ProcessId = [int]$process.ProcessId; IsAgent = $true } }
    }
    foreach ($target in @($targets | Sort-Object IsAgent -Descending)) {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($target.ProcessId)" -ErrorAction SilentlyContinue
        if (-not $current -or (Get-SswProcessOwnerSid -Process $current) -ne $ownerSid) { continue }
        $currentPath = [string]$current.ExecutablePath
        $pathIsOwned = $currentPath -and $currentPath.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)
        if (-not $pathIsOwned) { throw "PID 재사용으로 프로세스 소유권 검증에 실패했습니다: $($target.ProcessId)" }
        Stop-Process -Id $target.ProcessId -Force -ErrorAction Stop
    }
}

function Wait-SswBackgroundTaskStopped {
    param([ValidateRange(1, 60)][int]$TimeoutSeconds = 15)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        if (-not $task -or [string]$task.State -ne 'Running') { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "숨김 Agent 예약 작업이 제한 시간 안에 중지되지 않았습니다: $taskName"
}

function Write-SswAtomicText {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Content)

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$Path.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [IO.File]::WriteAllText($temporary, $Content, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporary, $Path, $replaceBackup, $true)
            if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
        }
        else { Move-Item -LiteralPath $temporary -Destination $Path }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
    }
}

Write-SswStep '현재 사용자 숨김 Agent 설치 전 검사'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw '현재 사용자 숨김 Agent는 Windows x64에서만 설치할 수 있습니다.'
}
if (Test-SswAdministrator) {
    throw '이 설치 방식은 일반 PowerShell에서 현재 사용자로 실행해야 합니다. 관리자 창을 닫고 다시 실행하세요.'
}
if (-not (Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue) -or
    (Get-Service -Name 'Schedule').Status -ne 'Running') {
    throw 'Windows 작업 스케줄러 서비스가 실행 중이어야 합니다. 진단 코드: TASK_SCHEDULER_UNAVAILABLE'
}
Import-Module ScheduledTasks -ErrorAction Stop
if (Get-Service -Name (Get-SswAgentServiceName) -ErrorAction SilentlyContinue) {
    throw 'Windows 서비스 Agent가 이미 설치되어 있습니다. 서비스 방식과 숨김 방식은 동시에 사용할 수 없습니다.'
}
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'SamsungSwitchWatch\AgentData'
if ($source.TrimEnd('\').Equals($install.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.'
}
foreach ($requiredPath in @($sourceExe, $sourceManifestPath, $sourceBaseConfigPath,
    (Join-Path $source 'run-agent-background.ps1'), (Join-Path $source 'common.ps1'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Agent 배포 파일을 찾지 못했습니다: $requiredPath" }
}

$sourceManifest = Read-SswJsonFile -Path $sourceManifestPath -Label 'Agent 패키지 빌드 매니페스트'
if ($sourceManifest.manifestVersion -ne 1 -or $sourceManifest.packageKind -ne 'Agent' -or
    $sourceManifest.executable.name -ne 'SamsungSwitchWatch.Agent.exe') {
    throw 'Agent 패키지 매니페스트 형식이 올바르지 않습니다.'
}
$manifestNames = New-Object Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($sourceManifest.files)) {
    $name = [string]$file.name
    if ([IO.Path]::GetFileName($name) -ne $name -or -not $manifestNames.Add($name)) {
        throw "Agent 패키지 매니페스트 파일 이름이 안전하지 않거나 중복되었습니다: $name"
    }
    $sourceFile = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Agent 패키지 파일이 없습니다: $name" }
    $actualHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Agent 패키지 SHA-256 불일치: $name" }
}
if (-not $manifestNames.Contains('run-agent-background.ps1')) { throw 'Agent 패키지에 숨김 실행기가 등록되어 있지 않습니다.' }
if ((Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) {
    throw 'Agent 실행 파일이 빌드 매니페스트의 SHA-256과 일치하지 않습니다.'
}

$sourceBaseConfig = Read-SswJsonFile -Path $sourceBaseConfigPath -Label 'Agent 기본 설정'
$sourceProductionConfig = if (Test-Path -LiteralPath $sourceProductionConfigPath -PathType Leaf) {
    Read-SswJsonFile -Path $sourceProductionConfigPath -Label 'Agent 운영 설정'
} else { $null }
$existingReceipt = $null
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    $existingReceipt = Read-SswJsonFile -Path $receiptPath -Label '현재 사용자 Agent 설치 영수증'
    $null = Assert-SswBackgroundAgentReceipt -Receipt $existingReceipt -InstallDirectory $install `
        -DataDirectory $data -OwnerSid $ownerSid
}
$existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($existingTask -and (-not $existingReceipt -or -not (Test-SswOwnedBackgroundTask -Task $existingTask))) {
    throw "동일한 이름의 예약 작업에 제품 소유권이 없어 변경하지 않습니다: $taskName"
}
$installExists = Test-Path -LiteralPath $install -PathType Container
if ($Repair -and (-not $installExists -or -not $existingReceipt)) {
    throw '복구 설치에는 유효한 현재 사용자 Agent 설치 폴더와 영수증이 필요합니다.'
}
if (-not $Repair -and ($installExists -or $existingTask)) {
    throw '현재 사용자 Agent가 이미 설치되어 있습니다. 업데이트 또는 복구에는 -Repair를 사용하세요.'
}

$dataEntries = @()
if (Test-Path -LiteralPath $data -PathType Container) {
    $dataEntries = @(Get-ChildItem -LiteralPath $data -Force)
}
if ($dataEntries.Count -gt 0 -and -not $existingReceipt) {
    throw "소유권 영수증이 없는 기존 데이터 폴더는 자동 사용하지 않습니다: $data"
}
$configurationSource = $null
if ($Repair) {
    if (-not (Test-Path -LiteralPath $installedProductionConfigPath -PathType Leaf)) {
        throw "설치된 운영 설정을 찾지 못했습니다: $installedProductionConfigPath"
    }
    $configurationSource = Read-SswJsonFile -Path $installedProductionConfigPath -Label '설치된 Agent 운영 설정'
}
elseif ($existingReceipt -and (Test-Path -LiteralPath $preservedConfigPath -PathType Leaf)) {
    $configurationSource = Read-SswJsonFile -Path $preservedConfigPath -Label '보존된 Agent 운영 설정'
}
elseif ($sourceProductionConfig) { $configurationSource = $sourceProductionConfig }
$migratingLegacyBackgroundState = $configurationSource -and
    ($configurationSource.Agent.PSObject.Properties['Switches'] -or
     $configurationSource.Agent.PSObject.Properties['EnablePolling'])
$productionConfiguration = New-SswBackgroundProductionConfiguration -ExistingConfiguration $configurationSource
$listenUrl = Get-SswAgentProperty -Configuration $productionConfiguration -Name 'ListenUrl'
if ([string]::IsNullOrWhiteSpace([string]$listenUrl)) {
    $listenUrl = Get-SswAgentProperty -Configuration $sourceBaseConfig -Name 'ListenUrl'
}
$listenUri = $null
if (-not [Uri]::TryCreate([string]$listenUrl, [UriKind]::Absolute, [ref]$listenUri) -or
    $listenUri.Scheme -ne 'https' -or $listenUri.Port -ne 18443) {
    throw 'Agent HTTPS ListenUrl must be fixed at TCP/18443.'
}
$httpPort = $listenUri.Port

$sourceDataValue = Get-SswAgentProperty -Configuration $sourceProductionConfig -Name 'DataDirectory'
if ([string]::IsNullOrWhiteSpace([string]$sourceDataValue)) {
    $sourceDataValue = Get-SswAgentProperty -Configuration $sourceBaseConfig -Name 'DataDirectory'
}
$sourceData = if ([IO.Path]::IsPathRooted([string]$sourceDataValue)) {
    [IO.Path]::GetFullPath([string]$sourceDataValue)
} else { [IO.Path]::GetFullPath((Join-Path $source ([string]$sourceDataValue))) }
$sourcePrefix = $source.TrimEnd('\') + '\'
$sourceDataIsPackageChild = $sourceData.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)
$sourceDataIsCurrentUserProduct = $false
try {
    Assert-SswProductPath -Path $sourceData -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'SamsungSwitchWatch\AgentData'
    $sourceDataIsCurrentUserProduct = $true
}
catch { $sourceDataIsCurrentUserProduct = $false }
if ((Test-Path -LiteralPath $sourceData -PathType Container) -and
    -not $sourceData.Equals($data, [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $sourceDataIsPackageChild -and -not $sourceDataIsCurrentUserProduct) {
        throw "현재 사용자 안전 범위 밖의 기존 Agent 데이터는 자동 이전하지 않습니다: $sourceData"
    }
    if ($sourceDataIsPackageChild) { Assert-SswNoReparsePoint -Parent $source -Child $sourceData }
    $sourceDataReparse = Get-ChildItem -LiteralPath $sourceData -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
    if ($sourceDataReparse) { throw "junction 또는 symlink가 포함된 기존 데이터는 자동 이전하지 않습니다: $($sourceDataReparse.FullName)" }
}

$agentProcesses = @(Get-SswAgentProcesses)
$allowedOwnedProcesses = @()
if ($Repair -and $existingTask -and (Test-SswOwnedBackgroundTask -Task $existingTask)) {
    $allowedOwnedProcesses = @($agentProcesses | Where-Object {
        $_.OwnerSid -eq $ownerSid -and $_.ExecutablePath -and
        $_.ExecutablePath.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)
    })
}
$foreignAgent = $agentProcesses | Where-Object { $_.ProcessId -notin @($allowedOwnedProcesses.ProcessId) } | Select-Object -First 1
if ($foreignAgent) {
    $displayPath = if ($foreignAgent.ExecutablePath) { $foreignAgent.ExecutablePath } else { '(경로 확인 불가)' }
    throw "수동 또는 다른 Agent가 실행 중입니다. 해당 창을 한 번 종료한 뒤 다시 실행하세요. PID=$($foreignAgent.ProcessId), Path=$displayPath"
}
if ($allowedOwnedProcesses.Count -eq 0 -and -not (Test-SswTcpPortAvailable -Port $httpPort)) {
    throw "Agent HTTPS port is already in use: TCP/$httpPort"
}

$actionArguments = Get-SswTaskActionArguments -InstalledRunnerPath $runnerPath
$taskAction = New-ScheduledTaskAction -Execute $powershellPath -Argument $actionArguments -WorkingDirectory $install
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $ownerIdentity.Name
$taskTrigger.Delay = 'PT15S'
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $ownerSid -LogonType Interactive -RunLevel Limited
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -Hidden -DontStopOnIdleEnd -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1))
$taskDefinition = New-ScheduledTask -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal `
    -Settings $taskSettings -Description $taskDescription
$null = Export-ScheduledTask -InputObject $taskDefinition

Write-Host "  source : $source"
Write-Host "  install: $install"
Write-Host "  data   : $data"
Write-Host "  task   : $taskName (현재 사용자, 숨김)"
Write-Host "  HTTPS  : TCP/$httpPort (existing firewall policy is unchanged)"
Write-Host "  target : $(@($productionConfiguration.Agent.AllowedTargetCidrs) -join ', ')"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 예약 작업·파일·방화벽은 변경되지 않았습니다.'
    return
}

$transactionId = [Guid]::NewGuid().ToString('N')
$installParent = Split-Path $install -Parent
$dataParent = Split-Path $data -Parent
$staging = "$install.__staging_$transactionId"
$backup = "$install.__backup_$transactionId"
$dataStaging = "$data.__staging_$transactionId"
$dataBackup = "$data.__backup_$transactionId"
$operationRoot = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch\Operations'
$transactionBackup = Join-Path $operationRoot "transactions\$transactionId"
$journalPath = Join-Path $operationRoot 'agent-background-install.json'
Assert-SswProductPath -Path $operationRoot -BaseRoot $env:LOCALAPPDATA `
    -ProductRelativeRoot 'SamsungSwitchWatch\Operations'
Assert-SswChildPath -Parent $operationRoot -Child $transactionBackup
$oldTaskXml = if ($existingTask) { Export-ScheduledTask -InputObject $existingTask } else { $null }
$oldTaskWasRunning = $existingTask -and [string]$existingTask.State -eq 'Running'
$installSwapped = $false
$dataSwapped = $false
$dataOriginalMoved = $false
$dataSnapshotTaken = $false
$taskRegistrationAttempted = $false
$transactionCommitted = $false
$fullDataSnapshot = Join-Path $transactionBackup 'data'

Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-install' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep '검증된 임시 폴더에 Agent 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $dataParent, $staging, $transactionBackup -Force | Out-Null
    foreach ($file in @($sourceManifest.files)) {
        Copy-Item -LiteralPath (Join-Path $source ([string]$file.name)) -Destination $staging -Force
    }
    Copy-Item -LiteralPath $sourceManifestPath -Destination $staging -Force
    [IO.File]::WriteAllText((Join-Path $staging 'appsettings.Production.json'),
        ($productionConfiguration | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))

    if ($existingTask) {
        Write-SswStep '기존 숨김 Agent 예약 작업 중지'
        Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        Wait-SswBackgroundTaskStopped
        Stop-SswOwnedBackgroundProcesses
    }
    if (-not (Test-SswTcpPortAvailable -Port $httpPort)) {
        throw "Agent HTTPS port is still in use after stopping the old task: TCP/$httpPort"
    }

    $initializeData = -not $existingReceipt
    if ($initializeData) {
        New-Item -ItemType Directory -Path $dataStaging -Force | Out-Null
        if ((Test-Path -LiteralPath $sourceData -PathType Container) -and
            -not $sourceData.Equals($data, [StringComparison]::OrdinalIgnoreCase)) {
            Get-ChildItem -LiteralPath $sourceData -Force | Copy-Item -Destination $dataStaging -Recurse -Force
        }
        if (Test-Path -LiteralPath $data -PathType Container) {
            Move-Item -LiteralPath $data -Destination $dataBackup
            $dataOriginalMoved = $true
        }
        Move-Item -LiteralPath $dataStaging -Destination $data
        $dataSwapped = $true
    }
    else {
        Copy-Item -LiteralPath $data -Destination $fullDataSnapshot -Recurse -Force
        $dataSnapshotTaken = $true
    }

    Write-SswStep 'Agent 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install -PathType Container) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    Write-SswStep '현재 사용자 로그인용 숨김 예약 작업 등록'
    $taskRegistrationAttempted = $true
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -InputObject $taskDefinition -Force | Out-Null
    $registeredTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
    if (-not (Test-SswOwnedBackgroundTask -Task $registeredTask)) { throw '등록된 숨김 Agent 예약 작업의 소유권 검증에 실패했습니다.' }

    Write-SswStep '숨김 Agent 시작 및 HTTPS liveness 확인'
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    $healthStatus = Invoke-SswLocalLivenessProbe -Port $httpPort -TimeoutSeconds 45 -UseHttps
    Write-Host "  liveness: $healthStatus"

    if ($migratingLegacyBackgroundState) {
        Write-SswStep 'Archive legacy Agent-owned credentials, database, and raw history'
        $legacyArchiveName = 'legacy-v0.7-backup-{0}-{1}' -f
            [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'),
            ([Guid]::NewGuid().ToString('N').Substring(0, 8))
        $legacyArchive = Join-Path $data $legacyArchiveName
        Assert-SswChildPath -Parent $data -Child $legacyArchive
        New-Item -ItemType Directory -Path $legacyArchive -Force | Out-Null
        $legacyCredentialDirectory = Join-Path $data 'credentials'
        if (Test-Path -LiteralPath $legacyCredentialDirectory -PathType Container) {
            Move-Item -LiteralPath $legacyCredentialDirectory -Destination $legacyArchive
        }
        foreach ($legacyDatabaseName in @('switchwatch.db', 'switchwatch.db-wal', 'switchwatch.db-shm')) {
            $legacyDatabasePath = Join-Path $data $legacyDatabaseName
            if (Test-Path -LiteralPath $legacyDatabasePath -PathType Leaf) {
                Move-Item -LiteralPath $legacyDatabasePath -Destination $legacyArchive
            }
        }
        foreach ($legacySchemaBackup in @(Get-ChildItem -LiteralPath $data `
            -Filter 'switchwatch.db.schema-*.bak' -File -ErrorAction SilentlyContinue)) {
            Move-Item -LiteralPath $legacySchemaBackup.FullName -Destination $legacyArchive
        }
        $archiveMetadata = [ordered]@{
            formatVersion = 1
            source = 'SamsungSwitchWatch Agent v0.7 or earlier'
            purpose = 'manual recovery or administrator-approved cleanup only'
            archivedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json
        Write-SswAtomicText -Path (Join-Path $legacyArchive 'README.json') -Content $archiveMetadata
        Write-Host "  legacy backup: $legacyArchive"
    }

    $productionJson = $productionConfiguration | ConvertTo-Json -Depth 20
    Write-SswAtomicText -Path $preservedConfigPath -Content $productionJson
    $receipt = [ordered]@{
        receiptVersion = 1
        product = 'SamsungSwitchWatchBackgroundAgent'
        mode = 'current-user-scheduled-task'
        ownerSid = $ownerSid
        taskName = $taskName
        installDirectory = $install
        dataDirectory = $data
        httpPort = $httpPort
        installedVersion = [string]$sourceManifest.version
        sourceCommit = [string]$sourceManifest.sourceCommit
        executableSha256 = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationSha256 = (Get-FileHash -LiteralPath $preservedConfigPath -Algorithm SHA256).Hash.ToLowerInvariant()
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 6
    Write-SswAtomicText -Path $receiptPath -Content $receipt
    $savedReceipt = Read-SswJsonFile -Path $receiptPath -Label '저장된 현재 사용자 Agent 설치 영수증'
    $null = Assert-SswBackgroundAgentReceipt -Receipt $savedReceipt -InstallDirectory $install `
        -DataDirectory $data -OwnerSid $ownerSid

    Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-install' -TransactionId $transactionId `
        -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true
    foreach ($obsolete in @($backup, $dataBackup, $transactionBackup)) {
        if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ''
    Write-Host '현재 사용자 숨김 Agent 설치가 완료되었습니다.' -ForegroundColor Green
    Write-Host 'RDP를 닫아도 같은 Windows 사용자가 로그인된 동안 창 없이 계속 실행됩니다.'
    Write-Warning '이 방식은 방화벽을 변경하지 않습니다. 현재 정상인 Viewer 연결 및 사내 방화벽 정책을 그대로 유지하십시오.' `
        -WarningAction Continue
}
catch {
    $failure = $_
    if ($transactionCommitted) { throw }
    Write-Warning '설치 실패를 감지해 현재 사용자 Agent 변경 사항을 복구합니다.' -WarningAction Continue
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-task'; Action = {
            $currentTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
            if ($currentTask -and ((Test-SswOwnedBackgroundTask -Task $currentTask) -or
                ($taskRegistrationAttempted -and (Test-SswBackgroundTaskRegistrationMarker -Task $currentTask)))) {
                Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
                Wait-SswBackgroundTaskStopped
            }
        } },
        [pscustomobject]@{ Name = 'stop-owned-processes'; Action = { Stop-SswOwnedBackgroundProcesses } },
        [pscustomobject]@{ Name = 'remove-new-task'; Action = {
            $currentTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
            if ($taskRegistrationAttempted -and $currentTask -and
                ((Test-SswOwnedBackgroundTask -Task $currentTask) -or
                 ($taskRegistrationAttempted -and (Test-SswBackgroundTaskRegistrationMarker -Task $currentTask)))) {
                Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
            }
        } },
        [pscustomobject]@{ Name = 'restore-data'; Action = {
            if ($dataSwapped -or $dataOriginalMoved) {
                if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
                if (Test-Path -LiteralPath $dataBackup) { Move-Item -LiteralPath $dataBackup -Destination $data }
            }
            elseif ($dataSnapshotTaken) {
                if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
                Move-Item -LiteralPath $fullDataSnapshot -Destination $data
            }
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restore-old-task'; Action = {
            if ($oldTaskXml) {
                Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Xml $oldTaskXml -Force | Out-Null
                if ($oldTaskWasRunning) { Start-ScheduledTask -TaskName $taskName -TaskPath '\' }
            }
        } },
        [pscustomobject]@{ Name = 'remove-transaction-artifacts'; Action = {
            foreach ($path in @($dataStaging, $transactionBackup)) {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            }
        } }
    ))
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-background-install' -TransactionId $transactionId `
        -Stage 'rollback-completed' -Status 'failed' -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) {
        Write-Warning ("일부 자동 복구 단계가 실패했습니다: {0}" -f ($rollbackErrors -join ', ')) -WarningAction Continue
    }
    throw $failure
}
