param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

function ConvertTo-SswSanitizedFirewallProfiles {
    param([AllowNull()][string]$Profile)

    $profiles = New-Object Collections.Generic.HashSet[string]([StringComparer]::Ordinal)
    foreach ($entry in @(([string]$Profile).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $sanitized = switch ($entry) {
            'Domain' { 'Domain' }
            'Private' { 'Private' }
            'Public' { 'Public' }
            'Any' { 'Any' }
            default { 'Unknown' }
        }
        $null = $profiles.Add($sanitized)
    }
    return @($profiles | Sort-Object)
}

function Get-SswAgentDiagnosticReport {
    param([Parameter(Mandatory = $true)][string]$ResolvedInstallDirectory)

    $serviceName = Get-SswAgentServiceName
    $install = [IO.Path]::GetFullPath($ResolvedInstallDirectory)
    $configPath = Join-Path $install 'appsettings.Production.json'
    $manifestPath = Join-Path $install 'BUILD-MANIFEST.json'
    $result = [ordered]@{
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'UNHEALTHY'
        app = [ordered]@{
            name = 'SamsungSwitchWatchAgent'
            version = 'UNKNOWN'
            manifestStatus = 'MANIFEST_NOT_FOUND'
        }
        service = [ordered]@{
            status = 'SERVICE_NOT_FOUND'
            startMode = 'UNKNOWN'
            exitCode = $null
            pathStatus = 'NOT_TESTED'
            accountStatus = 'NOT_TESTED'
            processStatus = 'NOT_TESTED'
        }
        listener = [ordered]@{
            port = 18443
            status = 'NOT_TESTED'
            configured = 'CONFIG_NOT_FOUND'
        }
        firewall = [ordered]@{
            status = 'FIREWALL_RULE_NOT_FOUND'
            enabled = $false
            profiles = @()
            exact = $false
        }
        network = [ordered]@{
            status = 'NOT_TESTED'
            activeCategories = @()
        }
        allowlists = [ordered]@{
            status = 'CONFIG_NOT_FOUND'
            managementCount = 0
            targetCount = 0
        }
        health = [ordered]@{
            live = 'NOT_TESTED'
            ready = 'NOT_TESTED'
        }
    }

    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $version = [string]$manifest.version
            if ([int]$manifest.manifestVersion -ne 1 -or
                [string]$manifest.packageKind -ne 'Agent' -or
                $version -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$') {
                throw 'Invalid Agent manifest.'
            }
            $result.app.version = $version
            $result.app.manifestStatus = 'OK'
        }
        catch {
            $result.app.manifestStatus = 'MANIFEST_INVALID'
        }
    }

    $service = Get-SswAgentServiceRuntimeSnapshot `
        -Name $serviceName `
        -InstalledExecutablePath (Join-Path $install 'SamsungSwitchWatch.Agent.exe')
    $result.service.status = [string]$service.Status
    $result.service.startMode = [string]$service.StartMode
    $result.service.exitCode = $service.ExitCode
    $result.service.pathStatus = [string]$service.PathStatus
    $result.service.accountStatus = [string]$service.AccountStatus
    $result.service.processStatus = [string]$service.ProcessStatus
    $result.listener.status = if ([int]$service.ProcessId -gt 0) {
        Get-SswTcpListenerStatus -Port 18443 -ExpectedProcessId ([int]$service.ProcessId)
    }
    else {
        'LISTENER_SERVICE_PROCESS_UNAVAILABLE'
    }

    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        try {
            $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $result.listener.configured = if ([string]$config.Agent.ListenUrl -eq
                'https://0.0.0.0:18443') {
                'EXACT'
            }
            else {
                'LISTENER_POLICY_MISMATCH'
            }
            $targetCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr @($config.Agent.AllowedTargetCidrs))
            if ($targetCidrs.Count -lt 1) {
                throw 'Target allowlist is empty.'
            }
            $result.allowlists.targetCount = $targetCidrs.Count
            $result.allowlists.status = 'MANAGEMENT_ALLOWLIST_NOT_TESTED'
        }
        catch {
            $result.listener.configured = if ($result.listener.configured -eq 'CONFIG_NOT_FOUND') {
                'CONFIG_INVALID'
            }
            else {
                $result.listener.configured
            }
            $result.allowlists.status = 'TARGET_ALLOWLIST_INVALID'
            $result.allowlists.targetCount = 0
        }
    }

    try {
        $firewall = Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
        if ($firewall) {
            $result.firewall.enabled = [string]$firewall.Enabled -eq 'True'
            $result.firewall.profiles = @(
                ConvertTo-SswSanitizedFirewallProfiles -Profile ([string]$firewall.Profile))
            try {
                $managementInputs = @($firewall.RemoteAddress | ForEach-Object {
                    $address = ([string]$_).Trim()
                    if ($address -match '/') { $address } else { "$address/32" }
                })
                $managementCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr $managementInputs)
                $result.allowlists.managementCount = $managementCidrs.Count
                $result.firewall.exact = [bool](
                    Test-SswAgentHttpsFirewallRuleExact -Snapshot $firewall `
                        -RemoteAddress $managementCidrs)
                $result.firewall.status = if ($result.firewall.exact) {
                    'OK'
                }
                else {
                    'FIREWALL_POLICY_MISMATCH'
                }
                if ($result.allowlists.status -eq 'MANAGEMENT_ALLOWLIST_NOT_TESTED' -and
                    $managementCidrs.Count -gt 0) {
                    $result.allowlists.status = 'OK'
                }
                elseif ($managementCidrs.Count -lt 1) {
                    $result.allowlists.status = 'MANAGEMENT_ALLOWLIST_INVALID'
                }
            }
            catch {
                $result.firewall.status = 'FIREWALL_POLICY_MISMATCH'
                $result.firewall.exact = $false
                $result.allowlists.managementCount = 0
                if ($result.allowlists.status -eq 'MANAGEMENT_ALLOWLIST_NOT_TESTED') {
                    $result.allowlists.status = 'MANAGEMENT_ALLOWLIST_INVALID'
                }
            }
        }
        elseif ($result.allowlists.status -eq 'MANAGEMENT_ALLOWLIST_NOT_TESTED') {
            $result.allowlists.status = 'MANAGEMENT_ALLOWLIST_NOT_FOUND'
        }
    }
    catch {
        $result.firewall.status = 'FIREWALL_QUERY_FAILED'
        $result.firewall.enabled = $false
        $result.firewall.exact = $false
        if ($result.allowlists.status -eq 'MANAGEMENT_ALLOWLIST_NOT_TESTED') {
            $result.allowlists.status = 'MANAGEMENT_ALLOWLIST_QUERY_FAILED'
        }
    }

    $network = Get-SswActiveNetworkCategorySnapshot
    $result.network.status = [string]$network.Status
    $result.network.activeCategories = @($network.Categories)

    try {
        $result.health.live = Invoke-SswLocalLivenessProbe -Port 18443 -TimeoutSeconds 5 -UseHttps
    }
    catch {
        $result.health.live = 'AGENT_LIVE_FAILED'
    }
    try {
        $result.health.ready = Invoke-SswLocalHealthProbe -Port 18443 -TimeoutSeconds 5 -UseHttps
    }
    catch {
        $result.health.ready = 'AGENT_READY_FAILED'
    }

    if ($result.app.manifestStatus -eq 'OK' -and
        $result.service.status -eq 'Running' -and
        $result.service.startMode -eq 'Auto' -and
        $result.service.exitCode -eq 0 -and
        $result.service.pathStatus -eq 'EXACT' -and
        $result.service.accountStatus -eq 'EXACT' -and
        $result.service.processStatus -eq 'AVAILABLE' -and
        $result.listener.status -eq 'LISTENING' -and
        $result.listener.configured -eq 'EXACT' -and
        $result.firewall.status -eq 'OK' -and
        $result.firewall.enabled -and
        $result.firewall.exact -and
        $result.network.status -eq 'ACTIVE_PROFILE_SUPPORTED' -and
        $result.allowlists.status -eq 'OK' -and
        $result.health.live -eq 'LIVE' -and
        $result.health.ready -eq 'READY') {
        $result.status = 'HEALTHY'
    }
    return [pscustomobject]$result
}

$result = Get-SswAgentDiagnosticReport `
    -ResolvedInstallDirectory ([IO.Path]::GetFullPath($InstallDirectory))
$json = $result | ConvertTo-Json -Depth 6
$json
if ($OutputPath) {
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        $json,
        (New-Object Text.UTF8Encoding($false)))
    Write-SswStep 'Sanitized diagnostic JSON saved'
}
if ($result.status -ne 'HEALTHY') {
    Write-Host 'AGENT_DIAGNOSTICS_UNHEALTHY: One or more sanitized Agent checks failed.'
    exit 1
}
