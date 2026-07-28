Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-HealthContract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Import-HealthContractFunction {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$Ast,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $definition = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq $Name
    }, $true))
    Assert-HealthContract -Condition ($definition.Count -eq 1) `
        -Message "Expected exactly one function named $Name."
    return [scriptblock]::Create($definition[0].Extent.Text)
}

function Get-ContractAst {
    param([Parameter(Mandatory = $true)][string]$Path)

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    Assert-HealthContract -Condition (@($errors).Count -eq 0) `
        -Message "PowerShell parse errors were found in $Path."
    return $ast
}

$installerPath = Join-Path $PSScriptRoot 'install-agent.ps1'
$diagnosticPath = Join-Path $PSScriptRoot 'diagnose-agent.ps1'
$installerText = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8
$diagnosticText = Get-Content -LiteralPath $diagnosticPath -Raw -Encoding UTF8
$commonText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'common.ps1') -Raw -Encoding UTF8
$installerAst = Get-ContractAst -Path $installerPath
$diagnosticAst = Get-ContractAst -Path $diagnosticPath

. (Import-HealthContractFunction -Ast $installerAst -Name 'Get-SswAgentRuntimeHealthAudit')
. (Import-HealthContractFunction -Ast $diagnosticAst -Name 'ConvertTo-SswSanitizedFirewallProfiles')
. (Import-HealthContractFunction -Ast $diagnosticAst -Name 'Get-SswAgentDiagnosticReport')

Write-SswStep 'Sanitized listener and active-profile helpers'
$script:listenerFixture = @(
    [pscustomobject]@{
        LocalPort = 443
        LocalAddress = '0.0.0.0'
        OwningProcess = 100
    },
    [pscustomobject]@{
        LocalPort = 18443
        LocalAddress = '0.0.0.0'
        OwningProcess = 4242
    })
$script:listenerQueryFails = $false
function Get-NetTCPConnection {
    param([object]$State, [object]$ErrorAction)
    if ($script:listenerQueryFails) { throw 'fixture listener query failure' }
    return @($script:listenerFixture)
}
Assert-HealthContract -Condition (
    (Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId 4242) -eq 'LISTENING'
) -Message 'The product-owned wildcard TCP/18443 listener was not detected.'
$script:listenerFixture = @([pscustomobject]@{
    LocalPort = 18443
    LocalAddress = '127.0.0.1'
    OwningProcess = 4242
})
Assert-HealthContract -Condition (
    (Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId 4242) -eq
        'LISTENER_ADDRESS_MISMATCH'
) -Message 'A loopback-only TCP/18443 listener was treated as the product listener.'
$script:listenerFixture = @([pscustomobject]@{
    LocalPort = 18443
    LocalAddress = '0.0.0.0'
    OwningProcess = 31337
})
Assert-HealthContract -Condition (
    (Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId 4242) -eq
        'LISTENER_PROCESS_MISMATCH'
) -Message 'A TCP/18443 listener owned by another process was treated as healthy.'
$script:listenerFixture = @([pscustomobject]@{
    LocalPort = 443
    LocalAddress = '0.0.0.0'
    OwningProcess = 4242
})
Assert-HealthContract -Condition (
    (Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId 4242) -eq 'NOT_LISTENING'
) -Message 'A missing TCP/18443 listener was treated as listening.'
$script:listenerQueryFails = $true
Assert-HealthContract -Condition (
    (Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId 4242) -eq
        'LISTENER_QUERY_FAILED'
) -Message 'A listener query failure was treated as success.'

$script:networkCategoryFixture = @('Public', 'Private')
$script:networkCategoryQueryFails = $false
function Get-NetConnectionProfile {
    param([object]$ErrorAction)
    if ($script:networkCategoryQueryFails) { throw 'fixture network query failure' }
    return @($script:networkCategoryFixture | ForEach-Object {
        [pscustomobject]@{
            Name = 'must-not-be-returned'
            InterfaceAlias = 'must-not-be-returned'
            NetworkCategory = $_
        }
    })
}
$network = Get-SswActiveNetworkCategorySnapshot
Assert-HealthContract -Condition (
    $network.Status -eq 'ACTIVE_PROFILE_SUPPORTED' -and
    ($network.Categories -join '|') -eq 'Private|Public'
) -Message 'A mixed Private/Public profile set was not accepted and sanitized.'
$script:networkCategoryFixture = @('Public')
$network = Get-SswActiveNetworkCategorySnapshot
Assert-HealthContract -Condition (
    $network.Status -eq 'ACTIVE_PROFILE_UNSUPPORTED' -and
    ($network.Categories -join '|') -eq 'Public'
) -Message 'A Public-only profile was treated as supported.'
$script:networkCategoryQueryFails = $true
$network = Get-SswActiveNetworkCategorySnapshot
Assert-HealthContract -Condition (
    $network.Status -eq 'ACTIVE_PROFILE_QUERY_FAILED' -and
    @($network.Categories).Count -eq 0
) -Message 'An active-profile query failure was treated as success.'

Write-SswStep 'Unchanged-policy Agent health gate'
$script:listenerStatus = 'LISTENING'
$script:activeProfileStatus = 'ACTIVE_PROFILE_SUPPORTED'
$script:liveFails = $false
$script:readyFails = $false
$script:liveProbeCount = 0
$script:readyProbeCount = 0
$script:installedExeFixture = Join-Path ([IO.Path]::GetTempPath()) 'SamsungSwitchWatch.Agent.exe'
$script:serviceFixture = [pscustomobject]@{
    State = 'Running'
    StartMode = 'Auto'
    ExitCode = 0
    PathName = "`"$script:installedExeFixture`" --service"
    StartName = 'NT SERVICE\SamsungSwitchWatchAgent'
    ProcessId = 4242
}
$script:firewallFixture = [pscustomobject]@{
    Name = 'SamsungSwitchWatchAgent-Https'
    DisplayName = 'Samsung Switch Watch Agent HTTPS'
    Group = 'Samsung Switch Watch'
    Description = 'Owned by SamsungSwitchWatchAgent installer v3'
    Enabled = 'True'
    Direction = 'Inbound'
    Action = 'Allow'
    Profile = 'Domain, Private'
    Protocol = 'TCP'
    LocalPort = '18443'
    RemotePort = 'Any'
    LocalAddress = @('Any')
    RemoteAddress = @('192.0.2.10/32')
    Program = 'Any'
    Service = 'Any'
    InterfaceType = 'Any'
}
function Get-SswTcpListenerStatus {
    param([int]$Port, [int]$ExpectedProcessId)
    $script:lastExpectedListenerProcessId = $ExpectedProcessId
    return $script:listenerStatus
}
function Get-CimInstance {
    param([object]$ClassName, [string]$Filter, [object]$ErrorAction)
    return $script:serviceFixture
}
function Get-SswAgentFirewallSnapshotByName {
    param([string]$Name)
    return $script:firewallFixture
}
function Get-SswActiveNetworkCategorySnapshot {
    return [pscustomobject]@{
        Status = $script:activeProfileStatus
        Categories = if ($script:activeProfileStatus -eq 'ACTIVE_PROFILE_SUPPORTED') {
            @('Private')
        }
        else {
            @('Public')
        }
    }
}
function Invoke-SswLocalLivenessProbe {
    param([int]$Port, [int]$TimeoutSeconds, [switch]$UseHttps)
    $script:liveProbeCount++
    if ($script:liveFails) { throw 'fixture liveness failure' }
    return 'LIVE'
}
function Invoke-SswLocalHealthProbe {
    param([int]$Port, [int]$TimeoutSeconds, [switch]$UseHttps)
    $script:readyProbeCount++
    if ($script:readyFails) { throw 'fixture readiness failure' }
    return 'READY'
}

$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition $audit.Healthy `
    -Message 'A fully healthy unchanged policy did not qualify for the no-op path.'
Assert-HealthContract -Condition (
    $audit.ServiceStartMode -eq 'Auto' -and
    $audit.ServicePath -eq 'EXACT' -and
    $audit.ServiceAccount -eq 'EXACT' -and
    $audit.ServiceProcess -eq 'AVAILABLE' -and
    $script:lastExpectedListenerProcessId -eq 4242 -and
    $script:liveProbeCount -eq 1 -and
    $script:readyProbeCount -eq 1
) -Message 'Service configuration, listener ownership, liveness, or readiness was not audited.'

$script:serviceFixture.StartMode = 'Manual'
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and $audit.ServiceStartMode -eq 'Manual'
) -Message 'A Manual Agent service qualified for the no-op path.'
$script:serviceFixture.StartMode = 'Auto'

$script:serviceFixture.PathName = '"C:\Program Files\Other\other.exe" --service'
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and $audit.ServicePath -eq 'MISMATCH'
) -Message 'An Agent service with the wrong executable path qualified for the no-op path.'
$script:serviceFixture.PathName = "`"$script:installedExeFixture`" --service"

$script:serviceFixture.StartName = 'LocalSystem'
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and $audit.ServiceAccount -eq 'MISMATCH'
) -Message 'An Agent service with the wrong account qualified for the no-op path.'
$script:serviceFixture.StartName = 'NT SERVICE\SamsungSwitchWatchAgent'

$script:liveProbeCount = 0
$script:readyProbeCount = 0
$script:liveFails = $true
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and
    $audit.Live -eq 'AGENT_LIVE_FAILED' -and
    $audit.Ready -eq 'READY' -and
    $script:liveProbeCount -eq 1 -and
    $script:readyProbeCount -eq 1
) -Message 'A liveness failure skipped readiness or qualified as healthy.'
$script:liveFails = $false
$script:activeProfileStatus = 'ACTIVE_PROFILE_UNSUPPORTED'
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (-not $audit.Healthy) `
    -Message 'A Public-only active profile qualified for the no-op path.'
$script:activeProfileStatus = 'ACTIVE_PROFILE_SUPPORTED'
$script:firewallFixture.Profile = 'Domain, Private, Public'
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and $audit.Firewall -eq 'FIREWALL_RULE_MISMATCH'
) -Message 'A firewall rule widened to Public qualified for the no-op path.'
$script:firewallFixture.Profile = 'Domain, Private'
$script:firewallFixture.RemoteAddress = @('LocalSubnet')
$audit = Get-SswAgentRuntimeHealthAudit -ServiceName 'SamsungSwitchWatchAgent' `
    -Port 18443 -ExpectedRemoteAddress @('192.0.2.10/32') `
    -InstalledExecutablePath $script:installedExeFixture
Assert-HealthContract -Condition (
    -not $audit.Healthy -and $audit.Firewall -eq 'FIREWALL_RULE_MISMATCH'
) -Message 'A LocalSubnet firewall rule qualified for the no-op path.'
$script:firewallFixture.RemoteAddress = @('192.0.2.10/32')

$healthAuditIndex = $installerText.IndexOf(
    '$unchangedPolicyHealth = Get-SswAgentRuntimeHealthAudit')
$healthyGuardIndex = $installerText.IndexOf(
    'if ($unchangedPolicyHealth.Healthy)',
    $healthAuditIndex)
$healthyReturnIndex = $installerText.IndexOf('return', $healthyGuardIndex)
$transactionIndex = $installerText.IndexOf(
    '$transactionId = [Guid]::NewGuid().ToString(''N'')')
Assert-HealthContract -Condition (
    $healthAuditIndex -ge 0 -and
    $healthyGuardIndex -gt $healthAuditIndex -and
    $healthyReturnIndex -gt $healthyGuardIndex -and
    $transactionIndex -gt $healthyReturnIndex
) -Message 'The unchanged policy still returns before the runtime health audit.'
foreach ($required in @(
    'AGENT_HEALTH_REAPPLY_REQUIRED',
    'AGENT_ACTIVE_PROFILE_UNSUPPORTED',
    'AGENT_POST_APPLY_HEALTH_FAILED')) {
    Assert-HealthContract -Condition $installerText.Contains($required) `
        -Message "The installer health contract is missing: $required"
}

Write-SswStep 'Sanitized Agent diagnostic report'
$script:readyFails = $false
$script:liveProbeCount = 0
$script:readyProbeCount = 0

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-health-contract-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $manifest = [ordered]@{
        manifestVersion = 1
        packageKind = 'Agent'
        version = '0.0.0-contract'
    } | ConvertTo-Json
    $configuration = [ordered]@{
        Agent = [ordered]@{
            AgentId = 'must-not-be-returned'
            ListenUrl = 'https://0.0.0.0:18443'
            DataDirectory = 'must-not-be-returned'
            AllowedTargetCidrs = @('198.51.100.20/32')
        }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        (Join-Path $fixtureRoot 'BUILD-MANIFEST.json'),
        $manifest,
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $fixtureRoot 'appsettings.Production.json'),
        $configuration,
        (New-Object Text.UTF8Encoding($false)))
    $script:serviceFixture.State = 'Running'
    $script:serviceFixture.StartMode = 'Auto'
    $script:serviceFixture.ExitCode = 0
    $script:serviceFixture.PathName = '"{0}" --service' -f
        (Join-Path $fixtureRoot 'SamsungSwitchWatch.Agent.exe')
    $script:serviceFixture.StartName = 'NT SERVICE\SamsungSwitchWatchAgent'
    $script:serviceFixture.ProcessId = 4242

    $report = Get-SswAgentDiagnosticReport -ResolvedInstallDirectory $fixtureRoot
    Assert-HealthContract -Condition (
        $report.status -eq 'HEALTHY' -and
        $report.app.version -eq '0.0.0-contract' -and
        $report.service.status -eq 'Running' -and
        $report.service.startMode -eq 'Auto' -and
        $report.service.exitCode -eq 0 -and
        $report.service.pathStatus -eq 'EXACT' -and
        $report.service.accountStatus -eq 'EXACT' -and
        $report.service.processStatus -eq 'AVAILABLE' -and
        $report.listener.status -eq 'LISTENING' -and
        $report.firewall.enabled -and
        $report.firewall.exact -and
        ($report.firewall.profiles -join '|') -eq 'Domain|Private' -and
        $report.allowlists.managementCount -eq 1 -and
        $report.allowlists.targetCount -eq 1 -and
        $report.health.live -eq 'LIVE' -and
        $report.health.ready -eq 'READY'
    ) -Message 'The healthy diagnostic report is missing required sanitized fields.'

    $json = $report | ConvertTo-Json -Depth 6
    foreach ($forbidden in @(
        '192.0.2.10',
        '198.51.100.20',
        'must-not-be-returned',
        'NT SERVICE\SamsungSwitchWatchAgent',
        '"PathName"',
        '"StartName"',
        '"ProcessId"',
        $fixtureRoot)) {
        Assert-HealthContract -Condition (-not $json.Contains($forbidden)) `
            -Message 'The diagnostic report exposed an address, path, identifier, or service principal.'
    }

    $script:serviceFixture.StartMode = 'Manual'
    $report = Get-SswAgentDiagnosticReport -ResolvedInstallDirectory $fixtureRoot
    Assert-HealthContract -Condition (
        $report.status -eq 'UNHEALTHY' -and
        $report.service.startMode -eq 'Manual'
    ) -Message 'Diagnostics treated a Manual Agent service as healthy.'
    $script:serviceFixture.StartMode = 'Auto'

    $script:serviceFixture.PathName = '"C:\Program Files\Other\other.exe" --service'
    $report = Get-SswAgentDiagnosticReport -ResolvedInstallDirectory $fixtureRoot
    Assert-HealthContract -Condition (
        $report.status -eq 'UNHEALTHY' -and
        $report.service.pathStatus -eq 'MISMATCH'
    ) -Message 'Diagnostics treated the wrong Agent service executable as healthy.'
    $script:serviceFixture.PathName = '"{0}" --service' -f
        (Join-Path $fixtureRoot 'SamsungSwitchWatch.Agent.exe')

    $script:serviceFixture.StartName = 'LocalSystem'
    $report = Get-SswAgentDiagnosticReport -ResolvedInstallDirectory $fixtureRoot
    Assert-HealthContract -Condition (
        $report.status -eq 'UNHEALTHY' -and
        $report.service.accountStatus -eq 'MISMATCH'
    ) -Message 'Diagnostics treated the wrong Agent service account as healthy.'
    $script:serviceFixture.StartName = 'NT SERVICE\SamsungSwitchWatchAgent'

    $script:readyFails = $true
    $script:liveProbeCount = 0
    $script:readyProbeCount = 0
    $report = Get-SswAgentDiagnosticReport -ResolvedInstallDirectory $fixtureRoot
    Assert-HealthContract -Condition (
        $report.status -eq 'UNHEALTHY' -and
        $report.health.live -eq 'LIVE' -and
        $report.health.ready -eq 'AGENT_READY_FAILED' -and
        $script:liveProbeCount -eq 1 -and
        $script:readyProbeCount -eq 1
    ) -Message 'A readiness failure was treated as success or collapsed into liveness.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $fixtureRoot
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-SswStep 'Unhealthy diagnostic subprocess exit contract'
$subprocessRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-diagnostic-subprocess-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $subprocessRoot | Out-Null
    $subprocessJsonPath = Join-Path $subprocessRoot 'diagnostic.json'
    $windowsPowerShell = Join-Path $env:SystemRoot `
        'System32\WindowsPowerShell\v1.0\powershell.exe'
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $windowsPowerShell
    $startInfo.Arguments = (
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        '-File "{0}" -InstallDirectory "{1}" -OutputPath "{2}"' -f
        $diagnosticPath,
        $subprocessRoot,
        $subprocessJsonPath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $null = $process.Start()
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()

    Assert-HealthContract -Condition ($exitCode -eq 1) `
        -Message "Unhealthy diagnostics returned exit code $exitCode instead of 1."
    Assert-HealthContract -Condition ([string]::IsNullOrWhiteSpace($standardError)) `
        -Message 'Unhealthy diagnostics wrote an error record that can expose a script path.'
    Assert-HealthContract -Condition (
        Test-Path -LiteralPath $subprocessJsonPath -PathType Leaf
    ) -Message 'Unhealthy diagnostics did not save JSON before exiting.'
    $subprocessJson = Get-Content -LiteralPath $subprocessJsonPath -Raw -Encoding UTF8
    $subprocessReport = $subprocessJson | ConvertFrom-Json
    Assert-HealthContract -Condition ($subprocessReport.status -eq 'UNHEALTHY') `
        -Message 'The subprocess JSON did not record the unhealthy result.'
    foreach ($forbidden in @($diagnosticPath, $subprocessRoot)) {
        Assert-HealthContract -Condition (
            -not $standardOutput.Contains($forbidden) -and
            -not $standardError.Contains($forbidden) -and
            -not $subprocessJson.Contains($forbidden)
        ) -Message 'The diagnostic subprocess exposed an absolute script or install path.'
    }
}
finally {
    if (Test-Path -LiteralPath $subprocessRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $subprocessRoot
        Remove-Item -LiteralPath $subprocessRoot -Recurse -Force
    }
}

foreach ($required in @(
    "name = 'SamsungSwitchWatchAgent'",
    'version = ''UNKNOWN''',
    'startMode = ''UNKNOWN''',
    'exitCode = $null',
    'pathStatus = ''NOT_TESTED''',
    'accountStatus = ''NOT_TESTED''',
    'processStatus = ''NOT_TESTED''',
    'managementCount = 0',
    'targetCount = 0',
    'activeCategories = @()',
    '$result.health.live',
    '$result.health.ready',
    'ConvertTo-Json -Depth 6',
    'exit 1',
    'AGENT_DIAGNOSTICS_UNHEALTHY')) {
    Assert-HealthContract -Condition $diagnosticText.Contains($required) `
        -Message "The diagnostic JSON contract is missing: $required"
}
Assert-HealthContract -Condition (
    -not $diagnosticText.Contains(
        "throw 'AGENT_DIAGNOSTICS_UNHEALTHY")
) -Message 'Unhealthy diagnostics still throw an error record with an absolute script path.'
Assert-HealthContract -Condition (
    $commonText.Contains('-Profile Domain,Private') -and
    -not $commonText.Contains('-Profile Domain,Private,Public') -and
    -not $commonText.Contains('-RemoteAddress LocalSubnet')
) -Message 'The Agent firewall contract widened beyond exact Domain/Private CIDRs.'

Write-SswStep 'Agent health and diagnostic contract passed'
