param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$AgentId = "agent-$env:COMPUTERNAME",
    [string[]]$ClientManagementCidrs,
    [string[]]$AllowedTargetCidrs,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$httpsPort = 18443
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$installedExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$installedConfigPath = Join-Path $install 'appsettings.Production.json'
$receiptPath = Join-Path $data 'install-receipt.json'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$isUpdate = $null -ne $existingService
$legacyBackgroundTaskName = Get-SswAgentBackgroundTaskName
$legacyBackgroundTaskDescription = 'Owned by SamsungSwitchWatch current-user background installer v1'
$legacyBackgroundInstall = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Agent'))
$legacyBackgroundData = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch\AgentData'))
$legacyBackgroundRunner = Join-Path $legacyBackgroundInstall 'run-agent-background.ps1'
$legacyBackgroundExe = Join-Path $legacyBackgroundInstall 'SamsungSwitchWatch.Agent.exe'
$legacyBackgroundReceiptPath = Join-Path $legacyBackgroundData 'background-install-receipt.json'
$legacyBackgroundOwnerSid = Get-SswCurrentUserSid
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Read-SswJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)
    try { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "$Label is invalid JSON: $($_.Exception.Message)" }
}

function Resolve-SswCidrInput {
    param(
        [AllowNull()][string[]]$Requested,
        [AllowNull()][string[]]$Preserved,
        [Parameter(Mandatory = $true)][string]$Prompt
    )

    if ($Requested -and @($Requested).Count -gt 0) {
        return @(ConvertTo-SswIpv4Cidrs -Cidr @($Requested))
    }
    if ($Preserved -and @($Preserved).Count -gt 0) {
        return @(ConvertTo-SswIpv4Cidrs -Cidr @($Preserved))
    }
    $answer = Read-Host $Prompt
    $entries = @($answer.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($entries.Count -eq 0) {
        throw 'CIDR 값이 비어 있습니다. 예시처럼 네트워크 주소와 /숫자를 함께 입력하세요.'
    }
    try { return @(ConvertTo-SswIpv4Cidrs -Cidr $entries) }
    catch { throw "CIDR 입력 형식이 올바르지 않습니다. 예: 10.20.30.0/24. 상세: $($_.Exception.Message)" }
}

function New-SswExecutorConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedAgentId,
        [Parameter(Mandatory = $true)][string[]]$TargetCidrs
    )

    return [ordered]@{
        Agent = [ordered]@{
            AgentId = $ResolvedAgentId
            ListenUrl = 'https://0.0.0.0:18443'
            DataDirectory = $data
            MockMode = $false
            AllowedTargetCidrs = @($TargetCidrs)
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

function Get-SswLegacyBackgroundTaskArguments {
    return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$legacyBackgroundRunner`" -InstallDirectory `"$legacyBackgroundInstall`""
}

function Get-SswProcessOwnerSid {
    param([Parameter(Mandatory = $true)][object]$Process)

    try {
        $owner = Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop
        return [string]$owner.Sid
    }
    catch { return $null }
}

function Get-SswOwnedLegacyBackgroundProcesses {
    $owned = @()
    foreach ($process in @(Get-CimInstance Win32_Process `
        -Filter "Name='SamsungSwitchWatch.Agent.exe'" -ErrorAction SilentlyContinue)) {
        $path = [string]$process.ExecutablePath
        if ((Get-SswProcessOwnerSid -Process $process) -eq $legacyBackgroundOwnerSid -and
            $path -and $path.Equals($legacyBackgroundExe, [StringComparison]::OrdinalIgnoreCase)) {
            $owned += $process
        }
    }
    return $owned
}

function Test-SswOwnedLegacyBackgroundTask {
    param([AllowNull()][object]$Task)

    if (-not $Task -or
        [string]$Task.TaskName -ne $legacyBackgroundTaskName -or
        [string]$Task.TaskPath -ne '\' -or
        [string]$Task.Description -ne $legacyBackgroundTaskDescription) {
        return $false
    }
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { return $false }
    try { $taskOwnerSid = ConvertTo-SswIdentitySid -Identity ([string]$Task.Principal.UserId) }
    catch { return $false }

    return $taskOwnerSid -eq $legacyBackgroundOwnerSid -and
        ([string]$Task.Principal.RunLevel -in @('Limited', 'LeastPrivilege')) -and
        ([string]$actions[0].Execute).Equals($windowsPowerShell, [StringComparison]::OrdinalIgnoreCase) -and
        ([string]$actions[0].Arguments).Equals(
            (Get-SswLegacyBackgroundTaskArguments), [StringComparison]::Ordinal) -and
        ([string]$actions[0].WorkingDirectory).TrimEnd('\').Equals(
            $legacyBackgroundInstall.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Get-SswLegacyBackgroundState {
    Import-Module ScheduledTasks -ErrorAction Stop
    Assert-SswProductPath -Path $legacyBackgroundInstall -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Agent'
    Assert-SswProductPath -Path $legacyBackgroundData -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'SamsungSwitchWatch\AgentData'
    $task = Get-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\' `
        -ErrorAction SilentlyContinue
    $ownedProcesses = @(Get-SswOwnedLegacyBackgroundProcesses)

    if (-not $task) {
        if ($ownedProcesses.Count -gt 0) {
            throw "소유 경로의 이전 Agent 프로세스가 있지만 예약 작업 '$legacyBackgroundTaskName'이 없습니다. 작업 관리자에서 '$legacyBackgroundExe' 프로세스를 종료하고 이전 설치 폴더를 확인한 뒤 다시 실행하세요."
        }
        return $null
    }
    if (-not (Test-SswOwnedLegacyBackgroundTask -Task $task)) {
        throw "예약 작업 '$legacyBackgroundTaskName'이 Samsung Switch Watch의 정확한 소유 작업과 일치하지 않습니다. 자동 변경하지 않았습니다. 작업 스케줄러에서 이름 충돌을 확인한 뒤 다시 실행하세요."
    }
    if (-not (Test-Path -LiteralPath $legacyBackgroundReceiptPath -PathType Leaf)) {
        throw "이전 Agent 예약 작업의 소유 영수증이 없어 자동 이관하지 않습니다: $legacyBackgroundReceiptPath. 기존 v0.7 설치 자료를 복구하거나 작업을 관리자 승인으로 정리한 뒤 다시 실행하세요."
    }
    $backgroundReceipt = Read-SswJson -Path $legacyBackgroundReceiptPath `
        -Label 'Legacy current-user Agent receipt'
    $null = Assert-SswBackgroundAgentReceipt -Receipt $backgroundReceipt `
        -InstallDirectory $legacyBackgroundInstall -DataDirectory $legacyBackgroundData `
        -OwnerSid $legacyBackgroundOwnerSid
    if (-not (Test-Path -LiteralPath $legacyBackgroundExe -PathType Leaf)) {
        throw "이전 Agent 실행 파일이 없어 예약 작업을 안전하게 이관할 수 없습니다: $legacyBackgroundExe"
    }
    if (-not (Test-Path -LiteralPath $legacyBackgroundRunner -PathType Leaf)) {
        throw "이전 Agent 숨김 실행기가 없어 실패 시 예약 작업을 복구할 수 없습니다: $legacyBackgroundRunner"
    }
    foreach ($legacyRoot in @($legacyBackgroundInstall, $legacyBackgroundData)) {
        if (-not (Test-Path -LiteralPath $legacyRoot -PathType Container)) {
            throw "이전 Agent 소유 폴더가 없어 안전하게 이관할 수 없습니다: $legacyRoot"
        }
        $reparse = Get-ChildItem -LiteralPath $legacyRoot -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($reparse) {
            throw "이전 Agent 폴더에 junction 또는 symlink가 있어 자동 이관하지 않습니다: $($reparse.FullName)"
        }
    }
    $actualLegacyExeHash = (Get-FileHash -LiteralPath $legacyBackgroundExe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualLegacyExeHash -ne ([string]$backgroundReceipt.executableSha256).ToLowerInvariant()) {
        throw '이전 Agent 실행 파일이 소유 영수증과 달라 예약 작업을 자동 변경하지 않습니다.'
    }
    $legacyManifestPath = Join-Path $legacyBackgroundInstall 'BUILD-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $legacyManifestPath -PathType Leaf)) {
        throw '이전 Agent 패키지 매니페스트가 없어 숨김 실행기 소유권을 검증할 수 없습니다.'
    }
    $legacyManifest = Read-SswJson -Path $legacyManifestPath -Label 'Legacy current-user Agent manifest'
    if ($legacyManifest.packageKind -ne 'Agent') {
        throw '이전 Agent 패키지 매니페스트의 제품 종류가 일치하지 않습니다.'
    }
    $runnerManifestEntries = @($legacyManifest.files | Where-Object {
        [string]$_.name -eq 'run-agent-background.ps1'
    })
    if ($runnerManifestEntries.Count -ne 1 -or
        [string]$runnerManifestEntries[0].sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $legacyBackgroundRunner -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        ([string]$runnerManifestEntries[0].sha256).ToLowerInvariant()) {
        throw '이전 Agent 숨김 실행기가 패키지 매니페스트와 달라 자동 이관하지 않습니다.'
    }

    $configurationPath = Join-Path $legacyBackgroundData 'background-appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw '이전 Agent 대상 CIDR 설정을 찾지 못해 예약 작업을 자동 이관하지 않습니다.'
    }
    if (-not $backgroundReceipt.PSObject.Properties['configurationSha256'] -or
        [string]$backgroundReceipt.configurationSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        ([string]$backgroundReceipt.configurationSha256).ToLowerInvariant()) {
        throw '이전 Agent 보존 설정이 소유 영수증과 달라 자동 이관하지 않습니다.'
    }
    $configuration = Read-SswJson -Path $configurationPath -Label 'Legacy current-user Agent configuration'
    $legacyTargetCidrs = if ($configuration.Agent.PSObject.Properties['AllowedTargetCidrs']) {
        @($configuration.Agent.AllowedTargetCidrs)
    }
    elseif ($configuration.Agent.PSObject.Properties['Switches']) {
        @($configuration.Agent.Switches | ForEach-Object {
            $hostAddress = [string]$_.Host
            if ($hostAddress -match '/') { $hostAddress } else { "$hostAddress/32" }
        })
    }
    else { @() }
    if ($legacyTargetCidrs.Count -eq 0) {
        throw '이전 Agent 대상 CIDR 또는 장비 IPv4 설정이 비어 있어 서비스 설치로 안전하게 이관할 수 없습니다.'
    }
    $identityMetadataPath = Join-Path $legacyBackgroundData 'agent-identity.json'
    $identityCertificatePath = Join-Path $legacyBackgroundData 'https-certificate.pfx.dpapi'
    $identityFileCount = @(@($identityMetadataPath, $identityCertificatePath) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }).Count
    if ($identityFileCount -eq 1) {
        throw '이전 Agent HTTPS 신원 파일이 불완전하여 자동 이관하지 않습니다.'
    }
    $installAclSnapshot = @(Get-SswDirectoryAclSnapshot -Path $legacyBackgroundInstall)
    $dataAclSnapshot = @(Get-SswDirectoryAclSnapshot -Path $legacyBackgroundData)

    return [pscustomobject]@{
        Task = $task
        TaskXml = Export-ScheduledTask -InputObject $task
        WasRunning = [string]$task.State -eq 'Running'
        AgentId = [string]$configuration.Agent.AgentId
        AllowedTargetCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr $legacyTargetCidrs)
        OwnedProcessCount = $ownedProcesses.Count
        InstallDirectory = $legacyBackgroundInstall
        DataDirectory = $legacyBackgroundData
        IdentityFilesAvailable = $identityFileCount -eq 2
        IdentityMetadataPath = $identityMetadataPath
        IdentityCertificatePath = $identityCertificatePath
        InstallAclSnapshot = $installAclSnapshot
        DataAclSnapshot = $dataAclSnapshot
    }
}

function Wait-SswLegacyBackgroundTaskStopped {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $task = Get-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\' `
            -ErrorAction SilentlyContinue
        if (-not $task -or [string]$task.State -ne 'Running') { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "이전 Agent 예약 작업이 제한 시간 안에 중지되지 않았습니다: $legacyBackgroundTaskName"
}

function Stop-SswOwnedLegacyBackgroundProcesses {
    foreach ($candidate in @(Get-SswOwnedLegacyBackgroundProcesses)) {
        $processId = [int]$candidate.ProcessId
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" `
            -ErrorAction SilentlyContinue
        if (-not $current) { continue }
        $currentPath = [string]$current.ExecutablePath
        if ((Get-SswProcessOwnerSid -Process $current) -ne $legacyBackgroundOwnerSid -or
            -not $currentPath -or
            -not $currentPath.Equals($legacyBackgroundExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "PID 재사용으로 이전 Agent 프로세스 소유권 검증에 실패했습니다: $processId"
        }
        Stop-Process -Id $processId -Force -ErrorAction Stop
    }
}

function Get-SswDirectoryAclSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $items = @((Get-Item -LiteralPath $root -Force)) +
        @(Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction Stop)
    return @($items | ForEach-Object {
        $relative = if ($_.FullName.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
            ''
        }
        else { $_.FullName.Substring($root.Length + 1) }
        [pscustomobject]@{
            RelativePath = $relative
            Sddl = (Get-Acl -LiteralPath $_.FullName).Sddl
        }
    })
}

function Restore-SswDirectoryAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Snapshot
    )

    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    foreach ($entry in @($Snapshot | Sort-Object { ([string]$_.RelativePath).Length })) {
        $target = if ([string]::IsNullOrEmpty([string]$entry.RelativePath)) {
            $root
        }
        else {
            $candidate = Join-Path $root ([string]$entry.RelativePath)
            Assert-SswChildPath -Parent $root -Child $candidate
            $candidate
        }
        if (-not (Test-Path -LiteralPath $target)) {
            throw "ACL 복구 대상이 없습니다: $target"
        }
        $acl = Get-Acl -LiteralPath $target
        $acl.SetSecurityDescriptorSddlForm([string]$entry.Sddl)
        Set-Acl -LiteralPath $target -AclObject $acl
    }
}

Write-SswStep 'Agent install-or-update preflight'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Samsung Switch Watch Agent requires Windows x64.'
}
Assert-SswAdministrator
if ($AgentId -notmatch '^[A-Za-z0-9_-]{1,64}$') {
    throw 'AgentId must contain only letters, digits, hyphen, or underscore (maximum 64 characters).'
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Agent executable is missing: $sourceExe" }
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { throw "Package manifest is missing: $sourceManifestPath" }
if ($source.TrimEnd('\').Equals($install.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Extract the release ZIP outside the Program Files install directory.'
}
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'

$sourceManifest = Read-SswJson -Path $sourceManifestPath -Label 'Agent package manifest'
if ($sourceManifest.manifestVersion -ne 1 -or $sourceManifest.packageKind -ne 'Agent' -or
    $sourceManifest.executable.name -ne 'SamsungSwitchWatch.Agent.exe') {
    throw 'The package manifest is not an Agent manifest.'
}
$manifestNames = New-Object Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($sourceManifest.files)) {
    $name = [string]$file.name
    if ([IO.Path]::GetFileName($name) -ne $name -or -not $manifestNames.Add($name)) {
        throw "Unsafe or duplicate package file name: $name"
    }
    $path = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file is missing: $name" }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Package hash mismatch: $name" }
}
if ((Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) {
    throw 'Agent executable hash does not match BUILD-MANIFEST.json.'
}

$existingConfig = $null
$existingReceipt = $null
$preservedClientCidrs = @()
$preservedTargetCidrs = @()
$migratingLegacyAgentState = $false
$legacyBackgroundState = Get-SswLegacyBackgroundState
if ($isUpdate -and $legacyBackgroundState) {
    throw "Windows 서비스와 이전 현재 사용자 예약 작업이 동시에 등록되어 있어 자동 이관하지 않습니다. 서비스 '$serviceName'과 예약 작업 '$legacyBackgroundTaskName' 중 실제 운영 중인 하나를 관리자가 확인·정리한 뒤 다시 실행하세요."
}
if ($isUpdate) {
    if (-not (Test-Path -LiteralPath $installedConfigPath -PathType Leaf)) {
        throw 'The existing service is missing its configuration.'
    }
    $existingConfig = Read-SswJson -Path $installedConfigPath -Label 'Installed Agent configuration'
    $configuredData = [IO.Path]::GetFullPath([string]$existingConfig.Agent.DataDirectory)
    Assert-SswProductPath -Path $configuredData -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
    if ($PSBoundParameters.ContainsKey('DataDirectory') -and
        -not $data.Equals($configuredData, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DataDirectory does not match the existing Agent configuration.'
    }
    $data = $configuredData
    $receiptPath = Join-Path $data 'install-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw 'The existing service is missing its install receipt.'
    }
    $existingReceipt = Read-SswJson -Path $receiptPath -Label 'Installed Agent receipt'
    if ([int]$existingReceipt.receiptVersion -eq 3) {
        $validatedReceipt = Assert-SswAgentExecutorReceipt -Receipt $existingReceipt `
            -InstallDirectory $install -DataDirectory $data
        $AgentId = $validatedReceipt.AgentId
        $preservedClientCidrs = @($validatedReceipt.ClientManagementCidrs)
        $preservedTargetCidrs = @($validatedReceipt.AllowedTargetCidrs)
    }
    else {
        $migratingLegacyAgentState = $true
        $legacySwitches = @($existingConfig.Agent.Switches)
        if ($legacySwitches.Count -lt 1) { throw 'Legacy Agent inventory is empty and cannot be migrated safely.' }
        $legacyInventoryHash = Get-SswSwitchInventoryHash -Switches $legacySwitches
        $null = Assert-SswAgentInstallReceipt -Receipt $existingReceipt `
            -AgentId ([string]$existingConfig.Agent.AgentId) `
            -SwitchInventoryHash $legacyInventoryHash -SwitchCount $legacySwitches.Count
        $AgentId = [string]$existingConfig.Agent.AgentId
        $legacyClientAddresses = if ($existingReceipt.PSObject.Properties['viewerRemoteAddresses']) {
            @($existingReceipt.viewerRemoteAddresses)
        } else { @() }
        if ($legacyClientAddresses.Count -eq 0) {
            $legacyFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
            if (-not $legacyFirewall) {
                $legacyFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
            }
            if ($legacyFirewall) { $legacyClientAddresses = @($legacyFirewall.RemoteAddress) }
        }
        $preservedClientCidrs = @($legacyClientAddresses | ForEach-Object {
            $address = [string]$_
            if ($address -match '/') { $address } else { "$address/32" }
        })
        $preservedTargetCidrs = @($legacySwitches | ForEach-Object {
            $hostAddress = [string]$_.Host
            if ($hostAddress -match '/') { $hostAddress } else { "$hostAddress/32" }
        })
        Write-Host '  migration: legacy inventory/firewall addresses will seed target and management CIDR gates'
    }
    if ([string]$existingConfig.Agent.ListenUrl -ne 'https://0.0.0.0:18443') {
        Write-Host '  migration: legacy listener will be replaced by fixed HTTPS/18443'
    }
}
elseif ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $receiptPath)) {
    throw 'Install remnants exist without the registered Agent service. Inspect or uninstall them before reinstalling.'
}
elseif ((Test-Path -LiteralPath $data -PathType Container) -and
    @(Get-ChildItem -LiteralPath $data -Force).Count -gt 0) {
    throw 'A non-empty Agent data directory exists without a registered service and valid receipt. Refusing to adopt unknown HTTPS identity data.'
}
if ($legacyBackgroundState) {
    if (-not $isUpdate) {
        if (-not [string]::IsNullOrWhiteSpace([string]$legacyBackgroundState.AgentId)) {
            $AgentId = [string]$legacyBackgroundState.AgentId
        }
        $preservedTargetCidrs = @($legacyBackgroundState.AllowedTargetCidrs)
    }
    Write-Host "  migration: exact owned current-user task '$legacyBackgroundTaskName' will be replaced by the Windows service"
    Write-Host '  migration: current-user program and data will move to an Administrators-only ProgramData archive'
}

$clientCidrs = @(Resolve-SswCidrInput -Requested $ClientManagementCidrs -Preserved $preservedClientCidrs `
    -Prompt 'Viewer PC가 있는 관리망 CIDR (예: 내 PC가 10.20.30.x이면 10.20.30.0/24, 여러 개는 쉼표로 구분)')
$targetCidrs = @(Resolve-SswCidrInput -Requested $AllowedTargetCidrs -Preserved $preservedTargetCidrs `
    -Prompt '스위치 관리 IP가 있는 CIDR (예: 10.40.0.0/16, 여러 개는 쉼표로 구분)')

$oldHttpFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
$oldHttpsFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
Assert-SswAgentFirewallGateReady -Port $httpsPort -AgentExecutablePath $installedExe

Write-Host "  작업 구분     : $(if ($isUpdate) { '기존 Agent 업데이트' } else { '신규 Agent 설치' })"
Write-Host "  Windows 서비스: $serviceName (창 없음, 자동 시작)"
Write-Host "  Viewer 연결   : HTTPS/TCP 18443"
Write-Host "  Viewer 관리망 : $($clientCidrs -join ', ')"
Write-Host "  스위치 대상망 : $($targetCidrs -join ', ')"
Write-Host "  보존 데이터   : $data"
if ($Preflight) {
    Write-SswStep 'Preflight passed; no files, services, or firewall rules were changed.'
    return
}

$transactionId = [Guid]::NewGuid().ToString('N')
$installParent = Split-Path $install -Parent
$operationsRoot = Join-Path $env:ProgramData 'SamsungSwitchWatch-Operations'
$transactionRoot = Join-Path $operationsRoot "transactions\$transactionId"
$staging = "$install.__staging_$transactionId"
$programBackup = "$install.__backup_$transactionId"
$dataSnapshot = Join-Path $transactionRoot 'data'
$journalPath = Join-Path $operationsRoot 'agent-install-or-update.json'
$serviceCreated = $false
$installSwapped = $false
$dataExisted = Test-Path -LiteralPath $data -PathType Container
$dataCreated = $false
$dataSnapshotTaken = $false
$firewallChanged = $false
$transactionCommitted = $false
$previousServiceWasRunning = $isUpdate -and $existingService.Status -eq 'Running'
$previousService = if ($isUpdate) { Get-CimInstance Win32_Service -Filter "Name='$serviceName'" } else { $null }
$previousUsesHttps = $isUpdate -and [string]$existingConfig.Agent.ListenUrl -like 'https://*'
$legacyBackgroundTaskTouched = $false
$legacyBackgroundTaskRemoved = $false
$legacyBackgroundArchive = $null
$legacyBackgroundProgramMoved = $false
$legacyBackgroundDataMoved = $false

Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
    -TransactionId $transactionId -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep 'Stage verified package'
    New-Item -ItemType Directory -Path $installParent, $staging, $transactionRoot -Force | Out-Null
    Set-SswInstallerBackupAcl -Path $transactionRoot
    foreach ($file in @($sourceManifest.files)) {
        Copy-Item -LiteralPath (Join-Path $source ([string]$file.name)) -Destination $staging -Force
    }
    $newConfig = New-SswExecutorConfiguration -ResolvedAgentId $AgentId -TargetCidrs $targetCidrs
    [IO.File]::WriteAllText((Join-Path $staging 'appsettings.Production.json'),
        ($newConfig | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))

    if ($legacyBackgroundState) {
        Write-SswStep 'Stop and unregister exact owned current-user Agent task'
        $currentLegacyTask = Get-ScheduledTask -TaskName $legacyBackgroundTaskName `
            -TaskPath '\' -ErrorAction SilentlyContinue
        if (-not (Test-SswOwnedLegacyBackgroundTask -Task $currentLegacyTask)) {
            throw '이관 직전 이전 Agent 예약 작업의 정확한 소유권 재검증에 실패했습니다.'
        }
        $legacyBackgroundTaskTouched = $true
        Stop-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\' `
            -ErrorAction SilentlyContinue
        Wait-SswLegacyBackgroundTaskStopped
        Stop-SswOwnedLegacyBackgroundProcesses
        $currentLegacyTask = Get-ScheduledTask -TaskName $legacyBackgroundTaskName `
            -TaskPath '\' -ErrorAction SilentlyContinue
        if (-not (Test-SswOwnedLegacyBackgroundTask -Task $currentLegacyTask)) {
            throw '제거 직전 이전 Agent 예약 작업의 정확한 소유권 재검증에 실패했습니다.'
        }
        Unregister-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\' `
            -Confirm:$false
        $legacyBackgroundTaskRemoved = $true
    }

    if ($isUpdate -and $existingService.Status -ne 'Stopped') {
        Write-SswStep 'Stop existing Agent service'
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    if (-not (Test-SswTcpPortAvailable -Port $httpsPort)) {
        throw 'TCP/18443 is still occupied after stopping the existing Agent.'
    }

    Write-SswStep 'Back up persistent Agent identity and configuration data'
    if ($dataExisted) {
        $reparse = Get-ChildItem -LiteralPath $data -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($reparse) { throw "Agent data contains a junction or symlink: $($reparse.FullName)" }
        Copy-Item -LiteralPath $data -Destination $dataSnapshot -Recurse -Force
        $dataSnapshotTaken = $true
    }
    else {
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $dataCreated = $true
    }
    if ($legacyBackgroundState -and $legacyBackgroundState.IdentityFilesAvailable) {
        Write-SswStep 'Preserve current-user Agent HTTPS identity'
        Copy-Item -LiteralPath $legacyBackgroundState.IdentityMetadataPath `
            -Destination (Join-Path $data 'agent-identity.json') -Force
        Copy-Item -LiteralPath $legacyBackgroundState.IdentityCertificatePath `
            -Destination (Join-Path $data 'https-certificate.pfx.dpapi') -Force
    }

    Write-SswStep 'Atomically swap Agent program files'
    if (Test-Path -LiteralPath $install -PathType Container) {
        Move-Item -LiteralPath $install -Destination $programBackup
    }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    $serviceBinPath = "`"$installedExe`" --service"
    if (-not $isUpdate) {
        & sc.exe create $serviceName "binPath= $serviceBinPath" 'start= auto' 'obj= NT AUTHORITY\LocalService' `
            'DisplayName= Samsung Switch Watch Agent' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows service registration failed.' }
        $serviceCreated = $true
    }
    else {
        & sc.exe config $serviceName "binPath= $serviceBinPath" 'start= auto' 'obj= NT AUTHORITY\LocalService' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows service update failed.' }
    }
    & sc.exe description $serviceName 'Windowless Samsung switch Telnet execution Agent' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows service description update failed.' }
    & sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows service recovery policy update failed.' }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows service failure flag update failed.' }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows service SID activation failed.' }

    $serviceSid = Get-SswServiceSid -Name $serviceName
    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid -ServiceRights Modify
    foreach ($existingLegacyArchive in @(Get-ChildItem -LiteralPath $data `
        -Directory -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -like 'legacy-v0.7-backup-*' -or
            $_.Name -like 'legacy-background-backup-*'
        })) {
        Assert-SswChildPath -Parent $data -Child $existingLegacyArchive.FullName
        Set-SswInstallerBackupAcl -Path $existingLegacyArchive.FullName
    }

    Write-SswStep 'Apply management-subnet HTTPS firewall rule'
    $firewallChanged = $true
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https' -AllowMissing
    New-SswAgentHttpsFirewallRule -RemoteAddress $clientCidrs
    $appliedFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
    if (-not $appliedFirewall -or
        -not (Test-SswAgentHttpsFirewallRuleExact -Snapshot $appliedFirewall -RemoteAddress $clientCidrs)) {
        throw 'The applied HTTPS firewall rule does not match the requested management CIDRs.'
    }

    Write-SswStep 'Start windowless service and verify HTTPS readiness'
    Start-Service -Name $serviceName
    $ready = Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60 -UseHttps
    Write-Host "  readiness     : $ready"

    if ($legacyBackgroundState) {
        Write-SswStep 'Move retired current-user Agent files to an Administrators-only archive'
        $legacyBackgroundArchiveName = 'legacy-background-backup-{0}-{1}' -f
            [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'),
            ([Guid]::NewGuid().ToString('N').Substring(0, 8))
        $legacyBackgroundArchive = Join-Path $data $legacyBackgroundArchiveName
        Assert-SswChildPath -Parent $data -Child $legacyBackgroundArchive
        New-Item -ItemType Directory -Path $legacyBackgroundArchive -Force | Out-Null
        Move-Item -LiteralPath $legacyBackgroundState.InstallDirectory `
            -Destination (Join-Path $legacyBackgroundArchive 'program')
        $legacyBackgroundProgramMoved = $true
        Move-Item -LiteralPath $legacyBackgroundState.DataDirectory `
            -Destination (Join-Path $legacyBackgroundArchive 'data')
        $legacyBackgroundDataMoved = $true
        $backgroundArchiveMetadata = [ordered]@{
            formatVersion = 1
            source = 'SamsungSwitchWatch current-user scheduled-task Agent'
            purpose = 'manual recovery or administrator-approved cleanup only'
            identityPreserved = [bool]$legacyBackgroundState.IdentityFilesAvailable
            archivedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $legacyBackgroundArchive 'README.json'),
            $backgroundArchiveMetadata, (New-Object Text.UTF8Encoding($false)))
        Set-SswInstallerBackupAcl -Path $legacyBackgroundArchive
        Write-Host "  legacy backup : $legacyBackgroundArchive"
    }

    if ($migratingLegacyAgentState) {
        Write-SswStep 'Archive legacy Agent-owned credentials, database, and raw history'
        $legacyArchiveName = 'legacy-v0.7-backup-{0}-{1}' -f
            [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'),
            ([Guid]::NewGuid().ToString('N').Substring(0, 8))
        $legacyArchive = Join-Path $data $legacyArchiveName
        Assert-SswChildPath -Parent $data -Child $legacyArchive
        New-Item -ItemType Directory -Path $legacyArchive -Force | Out-Null
        Set-SswInstallerBackupAcl -Path $legacyArchive
        $legacyInstalledConfig = Join-Path $programBackup 'appsettings.Production.json'
        if (-not (Test-Path -LiteralPath $legacyInstalledConfig -PathType Leaf)) {
            throw 'Legacy Agent inventory configuration is missing from the verified program backup.'
        }
        Copy-Item -LiteralPath $legacyInstalledConfig `
            -Destination (Join-Path $legacyArchive 'legacy-appsettings.Production.json') -Force
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
        [IO.File]::WriteAllText((Join-Path $legacyArchive 'README.json'), $archiveMetadata,
            (New-Object Text.UTF8Encoding($false)))
        Set-SswInstallerBackupAcl -Path $legacyArchive
        Write-Host "  legacy backup : $legacyArchive"
    }

    $receipt = [ordered]@{
        receiptVersion = 3
        product = 'SamsungSwitchWatchAgent'
        agentId = $AgentId
        installDirectory = $install
        dataDirectory = $data
        httpsPort = 18443
        clientManagementCidrs = @($clientCidrs)
        allowedTargetCidrs = @($targetCidrs)
        installedVersion = [string]$sourceManifest.version
        sourceCommit = [string]$sourceManifest.sourceCommit
        legacyBackgroundTaskMigrated = [bool]($null -ne $legacyBackgroundState)
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 8
    $temporaryReceipt = "$receiptPath.$transactionId.tmp"
    [IO.File]::WriteAllText($temporaryReceipt, $receipt, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        $receiptReplaceBackup = "$receiptPath.$transactionId.bak"
        [IO.File]::Replace($temporaryReceipt, $receiptPath, $receiptReplaceBackup, $true)
        Remove-Item -LiteralPath $receiptReplaceBackup -Force
    }
    else { Move-Item -LiteralPath $temporaryReceipt -Destination $receiptPath }

    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
        -TransactionId $transactionId -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true

    foreach ($obsolete in @($programBackup, $transactionRoot)) {
        if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ''
    Write-Host 'Samsung Switch Watch Agent 설치/업데이트가 완료되었습니다.' -ForegroundColor Green
    Write-Host 'Agent는 사용자에게 보이는 창 없이 Windows 서비스로 실행 중입니다.'
    Write-Host '스위치 IP와 ID/PW/enable PW는 Viewer에서만 등록하세요.'
    if ($legacyBackgroundState) {
        Write-Host "이전 현재 사용자 예약 작업은 제거했고 파일은 관리자 전용 보관 폴더로 옮겼습니다: $legacyBackgroundArchive"
    }
}
catch {
    $failure = $_
    if ($transactionCommitted) {
        Write-Warning "Install completed, but post-commit cleanup failed: $($failure.Exception.Message)"
        return
    }
    Write-Warning 'Install or update failed. Restoring the previous service, data, and firewall state.'
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-service'; Action = {
            $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($current -and $current.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force
                $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            }
        } },
        [pscustomobject]@{ Name = 'delete-new-service'; Action = {
            if ($serviceCreated) {
                & sc.exe delete $serviceName | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Service delete failed.' }
                Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
            }
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if ($installSwapped -and (Test-Path -LiteralPath $install)) {
                Remove-Item -LiteralPath $install -Recurse -Force
            }
            if (Test-Path -LiteralPath $programBackup) {
                Move-Item -LiteralPath $programBackup -Destination $install
            }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restore-service'; Action = {
            if ($isUpdate -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                $oldPath = [string]$previousService.PathName
                & sc.exe config $serviceName "binPath= $oldPath" 'start= auto' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Previous service configuration restore failed.' }
            }
        } },
        [pscustomobject]@{ Name = 'restore-legacy-background-files'; Action = {
            if ($legacyBackgroundArchive) {
                $archivedProgram = Join-Path $legacyBackgroundArchive 'program'
                $archivedData = Join-Path $legacyBackgroundArchive 'data'
                if ($legacyBackgroundProgramMoved) {
                    if (Test-Path -LiteralPath $legacyBackgroundState.InstallDirectory) {
                        throw 'Legacy background program restore target already exists.'
                    }
                    New-Item -ItemType Directory `
                        -Path (Split-Path $legacyBackgroundState.InstallDirectory -Parent) -Force | Out-Null
                    Move-Item -LiteralPath $archivedProgram `
                        -Destination $legacyBackgroundState.InstallDirectory
                    Restore-SswDirectoryAclSnapshot -Path $legacyBackgroundState.InstallDirectory `
                        -Snapshot @($legacyBackgroundState.InstallAclSnapshot)
                }
                if ($legacyBackgroundDataMoved) {
                    if (Test-Path -LiteralPath $legacyBackgroundState.DataDirectory) {
                        throw 'Legacy background data restore target already exists.'
                    }
                    New-Item -ItemType Directory `
                        -Path (Split-Path $legacyBackgroundState.DataDirectory -Parent) -Force | Out-Null
                    Move-Item -LiteralPath $archivedData `
                        -Destination $legacyBackgroundState.DataDirectory
                    Restore-SswDirectoryAclSnapshot -Path $legacyBackgroundState.DataDirectory `
                        -Snapshot @($legacyBackgroundState.DataAclSnapshot)
                }
                if (Test-Path -LiteralPath $legacyBackgroundArchive) {
                    Remove-Item -LiteralPath $legacyBackgroundArchive -Recurse -Force
                }
            }
        } },
        [pscustomobject]@{ Name = 'restore-data'; Action = {
            if ($dataCreated -and (Test-Path -LiteralPath $data)) {
                Remove-Item -LiteralPath $data -Recurse -Force
            }
            elseif ($dataSnapshotTaken) {
                if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
                Move-Item -LiteralPath $dataSnapshot -Destination $data
                if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
                    $oldServiceSid = Get-SswServiceSid -Name $serviceName
                    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $oldServiceSid -ServiceRights Modify
                    foreach ($restoredLegacyArchive in @(Get-ChildItem -LiteralPath $data `
                        -Directory -ErrorAction SilentlyContinue | Where-Object {
                            $_.Name -like 'legacy-v0.7-backup-*' -or
                            $_.Name -like 'legacy-background-backup-*'
                        })) {
                        Assert-SswChildPath -Parent $data -Child $restoredLegacyArchive.FullName
                        Set-SswInstallerBackupAcl -Path $restoredLegacyArchive.FullName
                    }
                }
            }
        } },
        [pscustomobject]@{ Name = 'restore-firewall'; Action = {
            if ($firewallChanged) {
                Restore-SswAgentFirewallSnapshots -Snapshots @($oldHttpFirewall, $oldHttpsFirewall)
            }
        } },
        [pscustomobject]@{ Name = 'restart-previous-service'; Action = {
            if ($isUpdate -and $previousServiceWasRunning -and
                (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                Start-Service -Name $serviceName
                if ($previousUsesHttps) {
                    $null = Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60 -UseHttps
                }
                else {
                    $null = Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60
                }
            }
        } },
        [pscustomobject]@{ Name = 'restore-legacy-background-task'; Action = {
            if ($legacyBackgroundTaskTouched) {
                $currentLegacyTask = Get-ScheduledTask -TaskName $legacyBackgroundTaskName `
                    -TaskPath '\' -ErrorAction SilentlyContinue
                if ($legacyBackgroundTaskRemoved) {
                    if ($currentLegacyTask) {
                        throw 'Rollback found an unexpected task with the legacy Agent task name.'
                    }
                    Register-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\' `
                        -Xml ([string]$legacyBackgroundState.TaskXml) -Force | Out-Null
                    $currentLegacyTask = Get-ScheduledTask -TaskName $legacyBackgroundTaskName `
                        -TaskPath '\' -ErrorAction Stop
                }
                if (-not (Test-SswOwnedLegacyBackgroundTask -Task $currentLegacyTask)) {
                    throw 'Rollback could not revalidate the restored legacy Agent task.'
                }
                if ($legacyBackgroundState.WasRunning) {
                    Start-ScheduledTask -TaskName $legacyBackgroundTaskName -TaskPath '\'
                }
            }
        } },
        [pscustomobject]@{ Name = 'remove-transaction-files'; Action = {
            if (Test-Path -LiteralPath $transactionRoot) {
                Remove-Item -LiteralPath $transactionRoot -Recurse -Force
            }
        } }
    ))
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
        -TransactionId $transactionId -Stage 'rollback-completed' -Status 'failed' `
        -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) {
        Write-Warning ("Rollback completed with errors: {0}" -f ($rollbackErrors -join ', '))
    }
    throw $failure
}
