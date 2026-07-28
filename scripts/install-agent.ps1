param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$AgentId = "agent-$env:COMPUTERNAME",
    [string[]]$ClientManagementCidrs,
    [string[]]$AllowedTargetCidrs,
    [string[]]$ClientManagementAddresses,
    [string[]]$AllowedTargetAddresses,
    [switch]$ReconfigureAddresses,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$virtualServiceAccount = "NT SERVICE\$serviceName"
$httpsPort = 18443
$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Agent.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$installedExe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$installedManifestPath = Join-Path $install 'BUILD-MANIFEST.json'
$installedConfigPath = Join-Path $install 'appsettings.Production.json'
$receiptPath = Join-Path $data 'install-receipt.json'
$operationsRoot = Join-Path $env:ProgramData 'SamsungSwitchWatch-Operations'
$journalPath = Join-Path $operationsRoot 'agent-install-or-update.json'
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

function New-SswServiceControlFailureMessage {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [AllowNull()][object[]]$Output
    )

    $detail = (@($Output) | ForEach-Object { [string]$_ }) -join ' '
    $detail = [regex]::Replace($detail, '[\x00-\x1F]+', ' ').Trim()
    if ($detail.Length -gt 500) { $detail = $detail.Substring(0, 500) + '...' }
    if (-not $detail) { $detail = 'No additional diagnostic was returned by Windows.' }
    return "$Stage failed (sc.exe exit code $ExitCode). $detail"
}

function ConvertTo-SswIpv4HostCidrs {
    param(
        [Parameter(Mandatory = $true)][string[]]$Address,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $entries = @($Address | ForEach-Object {
        ([string]$_).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    })
    try {
        $normalized = @(ConvertTo-SswViewerRemoteAddresses -Address $entries)
    }
    catch {
        throw "$Label 입력은 1~32개의 일반 IPv4만 허용합니다. CIDR, DNS 이름, IPv6와 선행 0은 사용할 수 없습니다. 상세: $($_.Exception.Message)"
    }
    return @($normalized | ForEach-Object { "$_/32" })
}

function Resolve-SswAddressPolicyInput {
    param(
        [AllowNull()][string[]]$RequestedAddresses,
        [AllowNull()][string[]]$RequestedCidrs,
        [AllowNull()][string[]]$PreservedCidrs,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$PromptEvenWhenPreserved,
        [switch]$AllowBlankPreserve
    )

    if ($RequestedAddresses -and @($RequestedAddresses).Count -gt 0 -and
        $RequestedCidrs -and @($RequestedCidrs).Count -gt 0) {
        throw "AGENT_ADDRESS_INPUT_CONFLICT: $Label 일반 IPv4와 고급 CIDR을 동시에 지정할 수 없습니다."
    }
    if ($RequestedAddresses -and @($RequestedAddresses).Count -gt 0) {
        return @(ConvertTo-SswIpv4HostCidrs -Address @($RequestedAddresses) -Label $Label)
    }
    if ($RequestedCidrs -and @($RequestedCidrs).Count -gt 0) {
        return @(ConvertTo-SswIpv4Cidrs -Cidr @($RequestedCidrs))
    }
    if (-not $PromptEvenWhenPreserved -and
        $PreservedCidrs -and @($PreservedCidrs).Count -gt 0) {
        return @(ConvertTo-SswIpv4Cidrs -Cidr @($PreservedCidrs))
    }

    $script:sswAddressInputPrompted = $true
    $answer = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($answer)) {
        if ($AllowBlankPreserve -and
            $PreservedCidrs -and @($PreservedCidrs).Count -gt 0) {
            return @(ConvertTo-SswIpv4Cidrs -Cidr @($PreservedCidrs))
        }
        throw "$Label 값이 비어 있습니다. 일반 IPv4를 입력하세요."
    }
    $entries = @($answer.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($entries.Count -eq 0) {
        throw "$Label 값이 비어 있습니다. 일반 IPv4를 입력하세요."
    }
    return @(ConvertTo-SswIpv4HostCidrs -Address $entries -Label $Label)
}

function Test-SswStringSetEqual {
    param(
        [AllowNull()][string[]]$Left,
        [AllowNull()][string[]]$Right
    )

    $normalizedLeft = @($Left | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $normalizedRight = @($Right | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    return ($normalizedLeft -join '|') -ceq ($normalizedRight -join '|')
}

function Get-SswAgentRuntimeHealthAudit {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRemoteAddress,
        [Parameter(Mandatory = $true)][string]$InstalledExecutablePath
    )

    $audit = [ordered]@{
        Service = 'SERVICE_NOT_FOUND'
        ServiceStartMode = 'UNKNOWN'
        ServiceExit = 'UNKNOWN'
        ServicePath = 'NOT_TESTED'
        ServiceAccount = 'NOT_TESTED'
        ServiceProcess = 'NOT_TESTED'
        Listener = 'NOT_TESTED'
        Firewall = 'NOT_TESTED'
        ActiveProfile = 'NOT_TESTED'
        Live = 'NOT_TESTED'
        Ready = 'NOT_TESTED'
        Healthy = $false
    }

    $service = Get-SswAgentServiceRuntimeSnapshot `
        -Name $ServiceName -InstalledExecutablePath $InstalledExecutablePath
    $audit.Service = [string]$service.Status
    $audit.ServiceStartMode = [string]$service.StartMode
    $audit.ServiceExit = if ($null -eq $service.ExitCode) {
        'UNKNOWN'
    }
    elseif ([int64]$service.ExitCode -eq 0) {
        'ZERO'
    }
    else {
        'NONZERO'
    }
    $audit.ServicePath = [string]$service.PathStatus
    $audit.ServiceAccount = [string]$service.AccountStatus
    $audit.ServiceProcess = [string]$service.ProcessStatus
    if ([int]$service.ProcessId -gt 0) {
        $audit.Listener = Get-SswTcpListenerStatus `
            -Port $Port -ExpectedProcessId ([int]$service.ProcessId)
    }
    else {
        $audit.Listener = 'LISTENER_SERVICE_PROCESS_UNAVAILABLE'
    }

    try {
        $firewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
        $audit.Firewall = if ($firewall -and
            (Test-SswAgentHttpsFirewallRuleExact -Snapshot $firewall `
                -RemoteAddress $ExpectedRemoteAddress)) {
            'FIREWALL_RULE_EXACT'
        }
        elseif ($firewall) {
            'FIREWALL_RULE_MISMATCH'
        }
        else {
            'FIREWALL_RULE_NOT_FOUND'
        }
    }
    catch {
        $audit.Firewall = 'FIREWALL_QUERY_FAILED'
    }

    $network = Get-SswActiveNetworkCategorySnapshot
    $audit.ActiveProfile = [string]$network.Status

    try {
        $audit.Live = Invoke-SswLocalLivenessProbe -Port $Port -TimeoutSeconds 5 -UseHttps
    }
    catch {
        $audit.Live = 'AGENT_LIVE_FAILED'
    }
    try {
        $audit.Ready = Invoke-SswLocalHealthProbe -Port $Port -TimeoutSeconds 5 -UseHttps
    }
    catch {
        $audit.Ready = 'AGENT_READY_FAILED'
    }

    $audit.Healthy =
        $audit.Service -eq 'Running' -and
        $audit.ServiceStartMode -eq 'Auto' -and
        $audit.ServiceExit -eq 'ZERO' -and
        $audit.ServicePath -eq 'EXACT' -and
        $audit.ServiceAccount -eq 'EXACT' -and
        $audit.ServiceProcess -eq 'AVAILABLE' -and
        $audit.Listener -eq 'LISTENING' -and
        $audit.Firewall -eq 'FIREWALL_RULE_EXACT' -and
        $audit.ActiveProfile -eq 'ACTIVE_PROFILE_SUPPORTED' -and
        $audit.Live -eq 'LIVE' -and
        $audit.Ready -eq 'READY'
    return [pscustomobject]$audit
}

function Assert-SswReconfigurationPackageMatch {
    param(
        [Parameter(Mandatory = $true)][object]$SourceManifest,
        [Parameter(Mandatory = $true)][object]$InstalledManifest,
        [Parameter(Mandatory = $true)][object]$InstallReceipt,
        [Parameter(Mandatory = $true)][string]$InstalledExecutablePath
    )

    try {
        $sourceVersion = [string]$SourceManifest.version
        $sourceCommit = [string]$SourceManifest.sourceCommit
        $sourceExeHash = [string]$SourceManifest.executable.sha256
        $installedManifestVersion = [int]$InstalledManifest.manifestVersion
        $installedPackageKind = [string]$InstalledManifest.packageKind
        $installedExeName = [string]$InstalledManifest.executable.name
        $installedVersion = [string]$InstalledManifest.version
        $installedCommit = [string]$InstalledManifest.sourceCommit
        $installedExeHash = [string]$InstalledManifest.executable.sha256
        $receiptVersion = [int]$InstallReceipt.receiptVersion
        $receiptInstalledVersion = [string]$InstallReceipt.installedVersion
        $receiptSourceCommit = [string]$InstallReceipt.sourceCommit
    }
    catch {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent 또는 재설정 패키지의 빌드 정보가 불완전합니다.'
    }
    if ([string]::IsNullOrWhiteSpace($sourceVersion) -or
        $sourceCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        $installedManifestVersion -ne 1 -or
        $installedPackageKind -ne 'Agent' -or
        $installedExeName -ne 'SamsungSwitchWatch.Agent.exe' -or
        [string]::IsNullOrWhiteSpace($installedVersion) -or
        $installedCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        $sourceExeHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $installedExeHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent 또는 재설정 패키지의 빌드 정보를 확인할 수 없습니다.'
    }
    if (-not [string]::Equals($sourceVersion, $installedVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($sourceCommit, $installedCommit, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($sourceExeHash, $installedExeHash, [StringComparison]::OrdinalIgnoreCase) -or
        $receiptVersion -ne 3 -or
        -not [string]::Equals(
            $receiptInstalledVersion,
            $installedVersion,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $receiptSourceCommit,
            $installedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent와 현재 패키지의 버전 또는 소스 커밋이 다릅니다. 설치에 사용한 같은 버전의 Agent ZIP에서 다시 실행하세요.'
    }
    try {
        $actualInstalledExeHash = if (Test-Path -LiteralPath $InstalledExecutablePath -PathType Leaf) {
            (Get-FileHash -LiteralPath $InstalledExecutablePath -Algorithm SHA256).Hash
        }
        else { $null }
    }
    catch { $actualInstalledExeHash = $null }
    if (-not [string]::Equals(
        [string]$actualInstalledExeHash,
        $installedExeHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent 실행 파일이 설치 빌드 정보와 일치하지 않습니다.'
    }
}

function Confirm-SswAddressPolicy {
    param([Parameter(Mandatory = $true)][bool]$Required)

    if (-not $Required) { return }
    $answer = (Read-Host '위 허용 IP 설정으로 계속하시겠습니까? [Y/N]').Trim()
    if ($answer -notmatch '^(?i:y|yes)$') {
        throw 'AGENT_ADDRESS_CONFIGURATION_CANCELLED: 사용자가 허용 IP 변경을 취소했습니다.'
    }
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
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData `
    -ProductRelativeRoot 'SamsungSwitchWatch' -RequireExactProductRoot

try {
    $sourceManifestBytes = [IO.File]::ReadAllBytes($sourceManifestPath)
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $sourceManifestJson = $strictUtf8.GetString($sourceManifestBytes)
    if ($sourceManifestJson.Length -gt 0 -and
        $sourceManifestJson[0] -eq [char]0xfeff) {
        $sourceManifestJson = $sourceManifestJson.Substring(1)
    }
    $sourceManifest = $sourceManifestJson | ConvertFrom-Json
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $sourceManifestHash = ([BitConverter]::ToString(
            $sha256.ComputeHash($sourceManifestBytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}
catch {
    throw "Agent package manifest is invalid JSON or UTF-8: $($_.Exception.Message)"
}
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

$deploymentLock = Enter-SswDeploymentLock -Product 'Agent'
try {
$operationsRoot = [IO.Path]::GetFullPath($operationsRoot)
Assert-SswProductPath -Path $operationsRoot -BaseRoot $env:ProgramData `
    -ProductRelativeRoot 'SamsungSwitchWatch-Operations'
Initialize-SswAgentOperationsRoot -OperationsRoot $operationsRoot
Assert-SswAgentDeploymentJournalsReady -OperationsRoot $operationsRoot
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$isUpdate = $null -ne $existingService
$existingServiceConfiguration = if ($isUpdate) {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
}
else { $null }
if ($isUpdate -and [string]$existingServiceConfiguration.StartName -notin @(
    'NT AUTHORITY\LocalService',
    $virtualServiceAccount
)) {
    throw "The existing Agent service account is not supported for automatic update: $([string]$existingServiceConfiguration.StartName)"
}
$existingConfig = $null
$existingReceipt = $null
$preservedClientCidrs = @()
$preservedTargetCidrs = @()
$script:sswAddressInputPrompted = $false
$migratingLegacyAgentState = $false
$legacyBackgroundState = Get-SswLegacyBackgroundState
Assert-SswAgentFirewallNameSafety
$oldHttpFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
$oldHttpsFirewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
if ($isUpdate -and $legacyBackgroundState) {
    throw "Windows 서비스와 이전 현재 사용자 예약 작업이 동시에 등록되어 있어 자동 이관하지 않습니다. 서비스 '$serviceName'과 예약 작업 '$legacyBackgroundTaskName' 중 실제 운영 중인 하나를 관리자가 확인·정리한 뒤 다시 실행하세요."
}
if ($isUpdate) {
    $null = Assert-SswTrustedDirectoryRootOwner -Path $install
    if (-not (Test-Path -LiteralPath $installedConfigPath -PathType Leaf)) {
        throw 'The existing service is missing its configuration.'
    }
    $existingConfig = Read-SswJson -Path $installedConfigPath -Label 'Installed Agent configuration'
    $configuredData = [IO.Path]::GetFullPath([string]$existingConfig.Agent.DataDirectory)
    Assert-SswProductPath -Path $configuredData -BaseRoot $env:ProgramData `
        -ProductRelativeRoot 'SamsungSwitchWatch' -RequireExactProductRoot
    if ($PSBoundParameters.ContainsKey('DataDirectory') -and
        -not $data.Equals($configuredData, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DataDirectory does not match the existing Agent configuration.'
    }
    $data = $configuredData
    $null = Assert-SswTrustedDirectoryRootOwner -Path $data
    $receiptPath = Join-Path $data 'install-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw 'The existing service is missing its install receipt.'
    }
    $receiptIsAdministratorsOnly = Test-SswAdministratorsOnlyFileAcl -Path $receiptPath
    if ($receiptIsAdministratorsOnly) {
        $existingReceipt = Read-SswJson -Path $receiptPath -Label 'Installed Agent receipt'
    }
    else {
        Write-Host '  migration: service-writable install receipt will be ignored and replaced by an Administrators-only receipt'
    }

    $AgentId = [string]$existingConfig.Agent.AgentId
    if ($AgentId -notmatch '^[A-Za-z0-9_-]{1,64}$') {
        throw 'The installed Agent configuration contains an invalid AgentId.'
    }
    $legacySwitches = if ($existingConfig.Agent.PSObject.Properties['Switches']) {
        @($existingConfig.Agent.Switches)
    }
    else { @() }
    if ($legacySwitches.Count -gt 0) {
        $migratingLegacyAgentState = $true
        $legacyInventoryHash = Get-SswSwitchInventoryHash -Switches $legacySwitches
        if ($existingReceipt) {
            if ([int]$existingReceipt.receiptVersion -eq 3) {
                throw 'The Administrators-only install receipt does not match the legacy Agent configuration.'
            }
            $null = Assert-SswAgentInstallReceipt -Receipt $existingReceipt `
                -AgentId $AgentId -SwitchInventoryHash $legacyInventoryHash `
                -SwitchCount $legacySwitches.Count
        }
        $preservedTargetCidrs = @($legacySwitches | ForEach-Object {
            $hostAddress = [string]$_.Host
            if ($hostAddress -match '/') { $hostAddress } else { "$hostAddress/32" }
        })
        Write-Host '  migration: legacy inventory/firewall addresses will seed target and management CIDR gates'
    }
    else {
        $configuredTargetCidrs = if (
            $existingConfig.Agent.PSObject.Properties['AllowedTargetCidrs']) {
            @($existingConfig.Agent.AllowedTargetCidrs)
        }
        else { @() }
        if ($configuredTargetCidrs.Count -lt 1) {
            throw 'The installed Agent configuration has no allowed target CIDRs.'
        }
        $preservedTargetCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr $configuredTargetCidrs)
        if ($existingReceipt) {
            if ([int]$existingReceipt.receiptVersion -ne 3) {
                throw 'The Administrators-only install receipt does not match the stateless Agent configuration.'
            }
            $null = Assert-SswAgentExecutorReceipt -Receipt $existingReceipt `
                -InstallDirectory $install -DataDirectory $data
        }
    }

    $existingManagementFirewall = if ($oldHttpsFirewall) {
        $oldHttpsFirewall
    }
    else {
        $oldHttpFirewall
    }
    if ($existingManagementFirewall) {
        $preservedClientCidrs = @($existingManagementFirewall.RemoteAddress | ForEach-Object {
            $address = [string]$_
            if ($address -match '/') { $address } else { "$address/32" }
        })
    }
    if ([string]$existingConfig.Agent.ListenUrl -ne 'https://0.0.0.0:18443') {
        Write-Host '  migration: legacy listener will be replaced by fixed HTTPS/18443'
    }
}
elseif ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $receiptPath)) {
    throw 'Install remnants exist without the registered Agent service. Inspect or uninstall them before reinstalling.'
}
elseif (Test-Path -LiteralPath $data -PathType Container) {
    $null = Assert-SswTrustedDirectoryRootOwner -Path $data
    throw 'An Agent data directory exists without a registered service and valid receipt. Refusing to adopt even an empty pre-existing directory.'
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

if ($ReconfigureAddresses) {
    if (-not $isUpdate) {
        throw 'AGENT_RECONFIGURE_REQUIRES_EXISTING_INSTALL: 허용 IP 재설정은 설치된 Windows 서비스 Agent에서만 사용할 수 있습니다.'
    }
    if ($legacyBackgroundState -or $migratingLegacyAgentState) {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 이전 Agent 구조는 허용 IP만 재설정할 수 없습니다. 먼저 일반 설치/업데이트를 완료하세요.'
    }
    if (-not $existingReceipt) {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 관리자 전용 설치 영수증을 확인할 수 없습니다. 같은 버전의 Agent를 먼저 정상 업데이트하세요.'
    }
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
        throw 'AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent의 BUILD-MANIFEST.json을 찾지 못했습니다.'
    }
    try {
        $installedManifest = Read-SswJson -Path $installedManifestPath -Label 'Installed Agent package manifest'
    }
    catch {
        throw "AGENT_RECONFIGURE_SOURCE_MISMATCH: 설치된 Agent의 빌드 정보를 읽지 못했습니다. $($_.Exception.Message)"
    }
    Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
        -InstalledManifest $installedManifest -InstallReceipt $existingReceipt `
        -InstalledExecutablePath $installedExe
}

$clientPrompt = if ($ReconfigureAddresses) {
    'Viewer PC IPv4 (예: 10.20.30.41, 여러 대는 쉼표, 비우면 기존 설정 유지)'
}
else {
    'Viewer PC IPv4 (예: 10.20.30.41, 여러 대는 쉼표로 구분)'
}
$targetPrompt = if ($ReconfigureAddresses) {
    '스위치 관리 IPv4 (예: 10.40.0.11, 여러 대는 쉼표, 비우면 기존 설정 유지)'
}
else {
    '스위치 관리 IPv4 (예: 10.40.0.11, 여러 대는 쉼표로 구분)'
}
$clientCidrs = @(Resolve-SswAddressPolicyInput `
    -RequestedAddresses $ClientManagementAddresses -RequestedCidrs $ClientManagementCidrs `
    -PreservedCidrs $preservedClientCidrs -Prompt $clientPrompt -Label 'Viewer PC IPv4' `
    -PromptEvenWhenPreserved:$ReconfigureAddresses -AllowBlankPreserve:$ReconfigureAddresses)
$targetCidrs = @(Resolve-SswAddressPolicyInput `
    -RequestedAddresses $AllowedTargetAddresses -RequestedCidrs $AllowedTargetCidrs `
    -PreservedCidrs $preservedTargetCidrs -Prompt $targetPrompt -Label '스위치 관리 IPv4' `
    -PromptEvenWhenPreserved:$ReconfigureAddresses -AllowBlankPreserve:$ReconfigureAddresses)

Assert-SswAgentFirewallGateReady -Port $httpsPort -AgentExecutablePath $installedExe

Write-Host "  작업 구분     : $(if ($ReconfigureAddresses) { 'Agent 허용 IP 재설정' } elseif ($isUpdate) { '기존 Agent 업데이트' } else { '신규 Agent 설치' })"
Write-Host "  Windows 서비스: $serviceName (창 없음, 자동 시작)"
Write-Host "  Viewer 연결   : HTTPS/TCP 18443"
Write-Host "  Viewer 관리망 : $($clientCidrs -join ', ')"
Write-Host "  스위치 대상망 : $($targetCidrs -join ', ')"
Write-Host "  보존 데이터   : $data"
Confirm-SswAddressPolicy -Required ([bool]$ReconfigureAddresses -or $script:sswAddressInputPrompted)
if ($ReconfigureAddresses -and
    (Test-SswStringSetEqual -Left $clientCidrs -Right $preservedClientCidrs) -and
    (Test-SswStringSetEqual -Left $targetCidrs -Right $preservedTargetCidrs)) {
    Write-SswStep '허용 IP 변경 사항이 없어 현재 Agent 상태를 점검합니다.'
    $unchangedPolicyHealth = Get-SswAgentRuntimeHealthAudit `
        -ServiceName $serviceName -Port $httpsPort -ExpectedRemoteAddress $clientCidrs `
        -InstalledExecutablePath $installedExe
    Write-Host ("  health audit   : service={0}; start={1}; path={2}; account={3}; process={4}; listener={5}; firewall={6}; profile={7}; live={8}; ready={9}" -f
        $unchangedPolicyHealth.Service,
        $unchangedPolicyHealth.ServiceStartMode,
        $unchangedPolicyHealth.ServicePath,
        $unchangedPolicyHealth.ServiceAccount,
        $unchangedPolicyHealth.ServiceProcess,
        $unchangedPolicyHealth.Listener,
        $unchangedPolicyHealth.Firewall,
        $unchangedPolicyHealth.ActiveProfile,
        $unchangedPolicyHealth.Live,
        $unchangedPolicyHealth.Ready)
    if ($unchangedPolicyHealth.Healthy) {
        Write-SswStep '허용 IP와 Agent 상태가 모두 정상이라 서비스와 방화벽을 변경하지 않았습니다.'
        return
    }
    Write-Warning 'AGENT_HEALTH_REAPPLY_REQUIRED: 허용 IP는 같지만 Agent 상태가 불완전하여 기존 설정을 보존한 채 설치 트랜잭션을 다시 적용합니다.'
}
if ($Preflight) {
    Write-SswStep 'Preflight passed; no Agent program, data, service, or firewall state was changed. The operations journal ACL may have been initialized.'
    return
}

$transactionId = [Guid]::NewGuid().ToString('N')
$serviceSid = Get-SswServiceSid -Name $serviceName
$installParent = Split-Path $install -Parent
$transactionRoot = Join-Path $operationsRoot "transactions\$transactionId"
$staging = "$install.__staging_$transactionId"
$programBackup = "$install.__backup_$transactionId"
$failedProgram = "$install.__failed_$transactionId"
$dataSnapshot = Join-Path $transactionRoot 'data'
$failedData = Join-Path $transactionRoot 'failed-active-data'
$serviceCreated = $false
$installSwapped = $false
$programBackupTaken = $false
$dataExisted = Test-Path -LiteralPath $data -PathType Container
$dataCreationAttempted = $false
$dataCreated = $false
$dataSnapshotTaken = $false
$firewallChanged = $false
$transactionCommitted = $false
$serviceQuiescenceRequired = $false
$previousServiceWasRunning = $false
$previousService = $existingServiceConfiguration
$previousServiceUsesLocalService = $isUpdate -and
    [string]$previousService.StartName -ieq 'NT AUTHORITY\LocalService'
$previousUsesHttps = $isUpdate -and [string]$existingConfig.Agent.ListenUrl -like 'https://*'
$legacyBackgroundTaskTouched = $false
$legacyBackgroundTaskRemoved = $false
$legacyBackgroundArchive = $null
$legacyBackgroundProgramMoveAttempted = $false
$legacyBackgroundProgramMoved = $false
$legacyBackgroundDataMoveAttempted = $false
$legacyBackgroundDataMoved = $false

Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
    -TransactionId $transactionId -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep 'Stage verified package'
    New-Item -ItemType Directory -Path $installParent, $staging, $transactionRoot -Force | Out-Null
    Set-SswInstallerBackupAcl -Path $staging
    Set-SswInstallerBackupAcl -Path $transactionRoot
    foreach ($file in @($sourceManifest.files)) {
        Copy-Item -LiteralPath (Join-Path $source ([string]$file.name)) -Destination $staging -Force
    }
    Copy-Item -LiteralPath $sourceManifestPath -Destination $staging -Force
    Write-SswStep 'Re-verify package inside protected staging'
    foreach ($file in @($sourceManifest.files)) {
        $stagedPath = Join-Path $staging ([string]$file.name)
        if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf)) {
            throw "Staged package file is missing: $([string]$file.name)"
        }
        $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($stagedHash -ne ([string]$file.sha256).ToLowerInvariant()) {
            throw "Staged package hash mismatch: $([string]$file.name)"
        }
    }
    $stagedExe = Join-Path $staging 'SamsungSwitchWatch.Agent.exe'
    if ((Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) {
        throw 'Staged Agent executable hash does not match the in-memory package manifest.'
    }
    $stagedManifestPath = Join-Path $staging 'BUILD-MANIFEST.json'
    if ((Get-FileHash -LiteralPath $stagedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $sourceManifestHash) {
        throw 'Staged Agent BUILD-MANIFEST.json does not match the verified source package.'
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

    $serviceAtMutationBoundary = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($isUpdate) {
        if (-not $serviceAtMutationBoundary) {
            throw 'The existing Agent service disappeared during installation preparation.'
        }
        $serviceConfigurationAtMutationBoundary = Get-CimInstance Win32_Service `
            -Filter "Name='$serviceName'" -ErrorAction Stop
        if ([string]$serviceConfigurationAtMutationBoundary.PathName -cne
                [string]$previousService.PathName -or
            [string]$serviceConfigurationAtMutationBoundary.StartName -ine
                [string]$previousService.StartName -or
            [string]$serviceConfigurationAtMutationBoundary.StartMode -cne
                [string]$previousService.StartMode) {
            throw 'The existing Agent service configuration changed during installation preparation. No Agent files were changed.'
        }
        $previousService = $serviceConfigurationAtMutationBoundary
        $previousServiceWasRunning = $serviceAtMutationBoundary.Status -eq 'Running'
        $previousServiceUsesLocalService =
            [string]$previousService.StartName -ieq 'NT AUTHORITY\LocalService'
    }
    elseif ($serviceAtMutationBoundary) {
        throw 'The Agent service appeared during installation preparation. No Agent files were changed.'
    }

    $serviceQuiescenceRequired = $true
    if ($isUpdate -and $serviceAtMutationBoundary.Status -ne 'Stopped') {
        Write-SswStep 'Stop existing Agent service'
        Stop-Service -Name $serviceName -Force
        $serviceAtMutationBoundary.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    if ($isUpdate) {
        $stoppedService = Get-Service -Name $serviceName -ErrorAction Stop
        if ($stoppedService.Status -ne 'Stopped') {
            throw 'The existing Agent service did not reach the stopped state.'
        }
    }
    if (-not (Test-SswTcpPortAvailable -Port $httpsPort)) {
        throw 'TCP/18443 is still occupied after stopping the existing Agent.'
    }

    Write-SswStep 'Secure and back up persistent Agent identity and configuration data'
    Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData `
        -ProductRelativeRoot 'SamsungSwitchWatch' -RequireExactProductRoot
    if (-not $dataExisted) {
        # Do not use -Force here. If another process creates the directory after
        # preflight, creation must fail instead of adopting an untrusted root.
        $dataCreationAttempted = $true
        New-Item -ItemType Directory -Path $data -ErrorAction Stop | Out-Null
    }
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid `
        -ServiceRights Modify -AllowServiceOwnedDescendants `
        -AllowLegacyLocalServiceOwnedDescendants:$previousServiceUsesLocalService
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        Set-SswAdministratorsOnlyFileAcl -Path $receiptPath
    }
    if (-not $dataExisted) {
        # Only treat the root as our rollback artifact after ownership and ACL
        # normalization succeeds. A raced replacement must never be deleted.
        $dataCreated = $true
    }
    if ($dataExisted) {
        Copy-Item -LiteralPath $data -Destination $dataSnapshot -Recurse -Force
        $dataSnapshotTaken = $true
        Set-SswInstallerBackupAcl -Path $dataSnapshot
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
        $programBackupTaken = $true
        Set-SswInstallerBackupAcl -Path $programBackup
    }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    # Windows PowerShell 5.1 strips unescaped embedded quotes when invoking a
    # native executable. Prefix the quotes with backslashes so sc.exe receives
    # one binPath value containing the literal executable-path quotes.
    $serviceBinPathForSc = '\"' + $installedExe + '\" --service'
    if (-not $isUpdate) {
        $serviceCreateOutput = @(& sc.exe create $serviceName 'binPath=' $serviceBinPathForSc 'start=' 'auto' `
            'obj=' $virtualServiceAccount `
            'DisplayName=' 'Samsung Switch Watch Agent' 2>&1)
        $serviceCreateExitCode = $LASTEXITCODE
        if ($serviceCreateExitCode -eq 0) { $serviceCreated = $true }
        if ($serviceCreateExitCode -ne 0) {
            throw (New-SswServiceControlFailureMessage `
                -Stage 'Windows service registration' -ExitCode $serviceCreateExitCode `
                -Output $serviceCreateOutput)
        }
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            throw 'Windows service registration returned success but the service is missing.'
        }
    }
    else {
        $serviceUpdateOutput = @(& sc.exe config $serviceName 'binPath=' $serviceBinPathForSc 'start=' 'auto' `
            'obj=' $virtualServiceAccount 2>&1)
        $serviceUpdateExitCode = $LASTEXITCODE
        if ($serviceUpdateExitCode -ne 0) {
            throw (New-SswServiceControlFailureMessage `
                -Stage 'Windows service update' -ExitCode $serviceUpdateExitCode `
                -Output $serviceUpdateOutput)
        }
    }
    $serviceDescriptionOutput = @(& sc.exe description $serviceName `
        'Windowless Samsung switch Telnet execution Agent' 2>&1)
    $serviceDescriptionExitCode = $LASTEXITCODE
    if ($serviceDescriptionExitCode -ne 0) {
        throw (New-SswServiceControlFailureMessage `
            -Stage 'Windows service description update' -ExitCode $serviceDescriptionExitCode `
            -Output $serviceDescriptionOutput)
    }
    $serviceRecoveryOutput = @(& sc.exe failure $serviceName 'reset=' '86400' `
        'actions=' 'restart/5000/restart/15000/restart/60000' 2>&1)
    $serviceRecoveryExitCode = $LASTEXITCODE
    if ($serviceRecoveryExitCode -ne 0) {
        throw (New-SswServiceControlFailureMessage `
            -Stage 'Windows service recovery policy update' -ExitCode $serviceRecoveryExitCode `
            -Output $serviceRecoveryOutput)
    }
    $serviceFailureFlagOutput = @(& sc.exe failureflag $serviceName 1 2>&1)
    $serviceFailureFlagExitCode = $LASTEXITCODE
    if ($serviceFailureFlagExitCode -ne 0) {
        throw (New-SswServiceControlFailureMessage `
            -Stage 'Windows service failure flag update' -ExitCode $serviceFailureFlagExitCode `
            -Output $serviceFailureFlagOutput)
    }
    $serviceSidOutput = @(& sc.exe sidtype $serviceName unrestricted 2>&1)
    $serviceSidExitCode = $LASTEXITCODE
    if ($serviceSidExitCode -ne 0) {
        throw (New-SswServiceControlFailureMessage `
            -Stage 'Windows service SID activation' -ExitCode $serviceSidExitCode `
            -Output $serviceSidOutput)
    }
    $expectedServicePath = "`"$installedExe`" --service"
    $appliedServiceConfiguration = Get-CimInstance Win32_Service `
        -Filter "Name='$serviceName'" -ErrorAction Stop
    if ([string]$appliedServiceConfiguration.PathName -cne $expectedServicePath -or
        [string]$appliedServiceConfiguration.StartName -ine $virtualServiceAccount -or
        [string]$appliedServiceConfiguration.StartMode -cne 'Auto') {
        throw 'Windows service registration postcondition failed.'
    }

    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid -ServiceRights ReadAndExecute
    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $serviceSid `
        -ServiceRights Modify -AllowServiceOwnedDescendants
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        Set-SswAdministratorsOnlyFileAcl -Path $receiptPath
    }
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
    Set-SswAdministratorsOnlyFileAcl -Path $receiptPath

    Write-SswStep 'Start windowless service and verify HTTPS readiness'
    Start-Service -Name $serviceName
    $ready = Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60 -UseHttps
    Write-Host "  readiness     : $ready"
    $appliedHealth = Get-SswAgentRuntimeHealthAudit `
        -ServiceName $serviceName -Port $httpsPort -ExpectedRemoteAddress $clientCidrs `
        -InstalledExecutablePath $installedExe
    if ($appliedHealth.ActiveProfile -eq 'ACTIVE_PROFILE_UNSUPPORTED') {
        throw 'AGENT_ACTIVE_PROFILE_UNSUPPORTED: 활성 네트워크가 Public 전용입니다. Agent 방화벽은 Domain/Private 전용으로 유지되며 Public 또는 LocalSubnet으로 확대하지 않았습니다.'
    }
    if (-not $appliedHealth.Healthy) {
        throw ("AGENT_POST_APPLY_HEALTH_FAILED: service={0}; start={1}; path={2}; account={3}; process={4}; listener={5}; firewall={6}; profile={7}; live={8}; ready={9}" -f
            $appliedHealth.Service,
            $appliedHealth.ServiceStartMode,
            $appliedHealth.ServicePath,
            $appliedHealth.ServiceAccount,
            $appliedHealth.ServiceProcess,
            $appliedHealth.Listener,
            $appliedHealth.Firewall,
            $appliedHealth.ActiveProfile,
            $appliedHealth.Live,
            $appliedHealth.Ready)
    }

    if ($legacyBackgroundState) {
        Write-SswStep 'Move retired current-user Agent files to an Administrators-only archive'
        $legacyBackgroundArchiveName = 'legacy-background-backup-{0}-{1}' -f
            [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'),
            ([Guid]::NewGuid().ToString('N').Substring(0, 8))
        $legacyBackgroundArchive = Join-Path $data $legacyBackgroundArchiveName
        Assert-SswChildPath -Parent $data -Child $legacyBackgroundArchive
        New-Item -ItemType Directory -Path $legacyBackgroundArchive -ErrorAction Stop | Out-Null
        Set-SswInstallerBackupAcl -Path $legacyBackgroundArchive
        $legacyBackgroundProgramDestination = Join-Path $legacyBackgroundArchive 'program'
        $legacyBackgroundProgramMoveAttempted = $true
        try {
            Move-Item -LiteralPath $legacyBackgroundState.InstallDirectory `
                -Destination $legacyBackgroundProgramDestination -ErrorAction Stop
            $legacyBackgroundProgramMoved = $true
        }
        catch {
            if (-not (Test-Path -LiteralPath $legacyBackgroundState.InstallDirectory) -and
                (Test-Path -LiteralPath $legacyBackgroundProgramDestination -PathType Container)) {
                $legacyBackgroundProgramMoved = $true
            }
            throw
        }
        $legacyBackgroundDataDestination = Join-Path $legacyBackgroundArchive 'data'
        $legacyBackgroundDataMoveAttempted = $true
        try {
            Move-Item -LiteralPath $legacyBackgroundState.DataDirectory `
                -Destination $legacyBackgroundDataDestination -ErrorAction Stop
            $legacyBackgroundDataMoved = $true
        }
        catch {
            if (-not (Test-Path -LiteralPath $legacyBackgroundState.DataDirectory) -and
                (Test-Path -LiteralPath $legacyBackgroundDataDestination -PathType Container)) {
                $legacyBackgroundDataMoved = $true
            }
            throw
        }
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

    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
        -TransactionId $transactionId -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true

    foreach ($obsolete in @($programBackup, $transactionRoot)) {
        if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ''
    if ($ReconfigureAddresses) {
        Write-Host 'Samsung Switch Watch Agent 허용 IP 재설정이 완료되었습니다.' -ForegroundColor Green
    }
    else {
        Write-Host 'Samsung Switch Watch Agent 설치/업데이트가 완료되었습니다.' -ForegroundColor Green
    }
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
    $rollbackState = [pscustomobject]@{
        ServiceQuiesced = $false
        ServiceRegistrationReady = $false
        ProgramRestored = $false
        ServiceConfigurationRestored = $false
        LegacyBackgroundFilesRestored = $false
        DataRestored = $false
        FirewallRestored = $false
    }
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-service'; Action = {
            if ($serviceQuiescenceRequired) {
                $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($current -and $current.Status -ne 'Stopped') {
                    Stop-Service -Name $serviceName -Force
                    $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
                }
                $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($current -and $current.Status -ne 'Stopped') {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Agent service did not stop; destructive rollback is blocked.'
                }
            }
            $rollbackState.ServiceQuiesced = $true
        } },
        [pscustomobject]@{ Name = 'delete-new-service'; Action = {
            if (-not $rollbackState.ServiceQuiesced) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Service stop was not confirmed; service deletion is blocked.'
            }
            if ($serviceCreated) {
                & sc.exe delete $serviceName | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Service delete failed.' }
                Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
                if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: New Agent service deletion was not confirmed.'
                }
            }
            elseif (-not $isUpdate -and
                (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: An unconfirmed service remains after failed creation; file rollback is blocked.'
            }
            elseif ($isUpdate -and
                -not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: The previous Agent service disappeared; file rollback is blocked.'
            }
            $rollbackState.ServiceRegistrationReady = $true
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if (-not $rollbackState.ServiceQuiesced -or
                -not $rollbackState.ServiceRegistrationReady) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Service quiescence was not confirmed; program rollback is blocked.'
            }
            $programRollbackDisposition = Get-SswProgramRollbackDisposition `
                -IsUpdate $isUpdate -InstallSwapped $installSwapped `
                -ProgramBackupTaken $programBackupTaken `
                -InstallExists (Test-Path -LiteralPath $install -PathType Container) `
                -ProgramBackupExists (Test-Path -LiteralPath $programBackup -PathType Container)
            switch ($programRollbackDisposition) {
                'RestoreBackup' {
                    Set-SswInstallerBackupAcl -Path $programBackup -ValidateExistingOwner
                    $null = Restore-SswDirectoryWithQuarantine `
                        -ActivePath $install -BackupPath $programBackup `
                        -QuarantinePath $failedProgram -BackupRequired
                    Set-SswRestrictedDirectoryAcl -Path $install -ServiceSid $serviceSid `
                        -ServiceRights ReadAndExecute -AllowServiceOwnedDescendants
                }
                'QuarantineNewInstall' {
                    $null = Restore-SswDirectoryWithQuarantine `
                        -ActivePath $install -BackupPath $programBackup `
                        -QuarantinePath $failedProgram
                }
                'AlreadyIntact' { }
                default {
                    throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Unsupported program rollback disposition: $programRollbackDisposition"
                }
            }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
            $rollbackState.ProgramRestored = $true
        } },
        [pscustomobject]@{ Name = 'restore-service'; Action = {
            if (-not $rollbackState.ProgramRestored) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Program rollback was not confirmed; service configuration rollback is blocked.'
            }
            if ($serviceQuiescenceRequired -and $isUpdate) {
                if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Previous Agent service is missing; configuration rollback is blocked.'
                }
                $oldPath = [string]$previousService.PathName
                $oldStartName = [string]$previousService.StartName
                $oldStartMode = [string]$previousService.StartMode
                $oldStartTypeForSc = switch ($oldStartMode) {
                    'Auto' { 'auto' }
                    'Manual' { 'demand' }
                    'Disabled' { 'disabled' }
                    default {
                        throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Unsupported previous service start mode: $oldStartMode"
                    }
                }
                $oldPathForSc = $oldPath.Replace('"', '\"')
                & sc.exe config $serviceName 'binPath=' $oldPathForSc 'start=' $oldStartTypeForSc `
                    'obj=' $oldStartName | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Previous service configuration restore failed.' }
                $restoredService = Get-CimInstance Win32_Service `
                    -Filter "Name='$serviceName'" -ErrorAction Stop
                if ([string]$restoredService.PathName -cne $oldPath -or
                    [string]$restoredService.StartName -ine $oldStartName -or
                    [string]$restoredService.StartMode -cne $oldStartMode) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Previous service configuration postcondition failed.'
                }
            }
            $rollbackState.ServiceConfigurationRestored = $true
        } },
        [pscustomobject]@{ Name = 'restore-legacy-background-files'; Action = {
            if (-not $rollbackState.ServiceConfigurationRestored) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Service configuration rollback was not confirmed; legacy file rollback is blocked.'
            }
            if ($legacyBackgroundArchive) {
                $archivedProgram = Join-Path $legacyBackgroundArchive 'program'
                $archivedData = Join-Path $legacyBackgroundArchive 'data'
                if ($legacyBackgroundProgramMoveAttempted -and
                    -not $legacyBackgroundProgramMoved) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Legacy program move was only partially completed; source and archive were preserved.'
                }
                if ($legacyBackgroundDataMoveAttempted -and
                    -not $legacyBackgroundDataMoved) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Legacy data move was only partially completed; source and archive were preserved.'
                }
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
                if (Test-Path -LiteralPath $archivedProgram) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Legacy program remains in the archive after rollback.'
                }
                if (Test-Path -LiteralPath $archivedData) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Legacy data remains in the archive after rollback.'
                }
                if (Test-Path -LiteralPath $legacyBackgroundArchive) {
                    Remove-Item -LiteralPath $legacyBackgroundArchive -Recurse -Force
                }
            }
            $rollbackState.LegacyBackgroundFilesRestored = $true
        } },
        [pscustomobject]@{ Name = 'restore-data'; Action = {
            if (-not $rollbackState.ServiceConfigurationRestored -or
                -not $rollbackState.LegacyBackgroundFilesRestored) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Prior rollback dependencies are incomplete; active Agent data is preserved.'
            }
            Assert-SswLegacyBackgroundRollbackReadyForDataRestore `
                -ArchivePath $legacyBackgroundArchive `
                -ProgramMoveAttempted $legacyBackgroundProgramMoveAttempted `
                -ProgramWasMoved $legacyBackgroundProgramMoved `
                -ProgramRestorePath $(if ($legacyBackgroundState) {
                    $legacyBackgroundState.InstallDirectory
                } else { $null }) `
                -DataMoveAttempted $legacyBackgroundDataMoveAttempted `
                -DataWasMoved $legacyBackgroundDataMoved `
                -DataRestorePath $(if ($legacyBackgroundState) {
                    $legacyBackgroundState.DataDirectory
                } else { $null })
            if ($dataCreationAttempted -and -not $dataCreated -and
                (Test-Path -LiteralPath $data)) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 신규 Agent 데이터 폴더 생성 또는 ACL 적용 완료 여부가 불명확해 해당 폴더를 보존했습니다.'
            }
            if ($dataCreated -or $dataSnapshotTaken) {
                if ($dataSnapshotTaken) {
                    Set-SswInstallerBackupAcl -Path $dataSnapshot -ValidateExistingOwner
                }
                $null = Restore-SswDirectoryWithQuarantine `
                    -ActivePath $data -BackupPath $dataSnapshot `
                    -QuarantinePath $failedData -BackupRequired:$dataSnapshotTaken
            }
            if ($dataSnapshotTaken) {
                if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
                    $oldServiceSid = Get-SswServiceSid -Name $serviceName
                    Set-SswRestrictedDirectoryAcl -Path $data -ServiceSid $oldServiceSid `
                        -ServiceRights Modify -AllowServiceOwnedDescendants
                    $restoredReceiptPath = Join-Path $data 'install-receipt.json'
                    if (Test-Path -LiteralPath $restoredReceiptPath -PathType Leaf) {
                        Set-SswAdministratorsOnlyFileAcl -Path $restoredReceiptPath
                    }
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
            $rollbackState.DataRestored = $true
        } },
        [pscustomobject]@{ Name = 'restore-firewall'; Action = {
            if ($firewallChanged) {
                Restore-SswAgentFirewallSnapshots -Snapshots @($oldHttpFirewall, $oldHttpsFirewall)
            }
            $rollbackState.FirewallRestored = $true
        } },
        [pscustomobject]@{ Name = 'restart-previous-service'; Action = {
            if (-not $rollbackState.ServiceConfigurationRestored -or
                -not $rollbackState.ProgramRestored -or
                -not $rollbackState.DataRestored -or
                -not $rollbackState.FirewallRestored) {
                throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Previous Agent state was not fully restored; service restart is blocked.'
            }
            if ($serviceQuiescenceRequired -and $isUpdate -and
                $previousServiceWasRunning -and
                (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                $restoredServiceStatus = Get-Service -Name $serviceName -ErrorAction Stop
                if ($restoredServiceStatus.Status -ne 'Running') {
                    Start-Service -Name $serviceName
                }
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
                if (-not $rollbackState.LegacyBackgroundFilesRestored -or
                    -not $rollbackState.DataRestored -or
                    -not $rollbackState.FirewallRestored) {
                    throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: Legacy Agent files were not fully restored; task restart is blocked.'
                }
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
        } }
    ))
    if ($rollbackErrors.Count -eq 0) {
        foreach ($rollbackArtifact in @($failedProgram, $transactionRoot)) {
            if (-not (Test-Path -LiteralPath $rollbackArtifact)) { continue }
            try {
                Remove-Item -LiteralPath $rollbackArtifact -Recurse -Force
            }
            catch {
                $rollbackErrors += 'REMOVE_TRANSACTION_FILES_FAILED'
                Write-Warning "복구 증거 폴더를 정리하지 못해 보존했습니다: $rollbackArtifact"
                break
            }
        }
    }
    else {
        foreach ($rollbackArtifact in @($failedProgram, $transactionRoot)) {
            if (Test-Path -LiteralPath $rollbackArtifact) {
                Write-Warning "복구 오류 때문에 원본 snapshot과 증거를 보존했습니다: $rollbackArtifact"
            }
        }
    }
    Write-SswOperationJournal -Path $journalPath -Operation 'agent-install-or-update' `
        -TransactionId $transactionId -Stage 'rollback-completed' -Status 'failed' `
        -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) {
        Write-Warning ("Rollback completed with errors: {0}" -f ($rollbackErrors -join ', '))
    }
    throw $failure
}
}
finally {
    Exit-SswDeploymentLock -Lock $deploymentLock
}
