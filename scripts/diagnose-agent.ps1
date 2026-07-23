param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$configPath = Join-Path $install 'appsettings.Production.json'
$result = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    service = 'SERVICE_NOT_FOUND'
    configuration = 'CONFIG_NOT_FOUND'
    listener = 'NOT_TESTED'
    targetAllowlist = 'NOT_TESTED'
    agentLive = 'NOT_TESTED'
    agentReady = 'NOT_TESTED'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) { $result.service = if ($service.Status -eq 'Running') { 'OK' } else { 'AGENT_SERVICE_STOPPED' } }

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $result.configuration = 'OK'
        $result.listener = if ([string]$config.Agent.ListenUrl -eq 'https://0.0.0.0:18443') {
            'OK'
        } else { 'LISTENER_POLICY_MISMATCH' }
        try {
            $null = ConvertTo-SswIpv4Cidrs -Cidr @($config.Agent.AllowedTargetCidrs)
            $result.targetAllowlist = 'OK'
        }
        catch { $result.targetAllowlist = 'TARGET_CIDR_INVALID' }

        try {
            $result.agentLive = Invoke-SswLocalLivenessProbe -Port 18443 -TimeoutSeconds 5 -UseHttps
            $result.agentReady = Invoke-SswLocalHealthProbe -Port 18443 -TimeoutSeconds 5 -UseHttps
        }
        catch {
            $result.agentLive = 'AGENT_HTTPS_UNREACHABLE'
            $result.agentReady = 'AGENT_HTTPS_UNREACHABLE'
        }
    }
    catch { $result.configuration = 'CONFIG_INVALID' }
}

$json = $result | ConvertTo-Json
$json
if ($OutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, (New-Object Text.UTF8Encoding($false)))
    Write-SswStep 'Sanitized diagnostic JSON saved'
}
