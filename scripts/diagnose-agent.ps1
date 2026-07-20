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
    certificate = 'CERT_NOT_FOUND'
    database = 'DATABASE_NOT_FOUND'
    switchTcp = 'NOT_TESTED'
    agentLive = 'NOT_TESTED'
    agentReady = 'NOT_TESTED'
    agentHttps = 'NOT_TESTED'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) { $result.service = if ($service.Status -eq 'Running') { 'OK' } else { 'AGENT_SERVICE_STOPPED' } }

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $result.configuration = 'OK'
        $certificatePath = [string]$config.Agent.Https.CertificatePath
        $result.certificate = if (Test-Path -LiteralPath $certificatePath -PathType Leaf) { 'OK' } else { 'CERT_NOT_FOUND' }
        $databasePath = Join-Path ([string]$config.Agent.DataDirectory) 'switchwatch.db'
        $result.database = if (Test-Path -LiteralPath $databasePath -PathType Leaf) { 'OK' } else { 'DATABASE_NOT_FOUND' }

        $tcp = New-Object Net.Sockets.TcpClient
        try {
            $connect = $tcp.BeginConnect([string]$config.Agent.Switches[0].Host, [int]$config.Agent.Switches[0].Port, $null, $null)
            if ($connect.AsyncWaitHandle.WaitOne(5000) -and $tcp.Connected) {
                $tcp.EndConnect($connect)
                $result.switchTcp = 'OK'
            }
            else { $result.switchTcp = 'TCP_TIMEOUT' }
        }
        catch { $result.switchTcp = 'TCP_CONNECT_FAILED' }
        finally { $tcp.Dispose() }

        $handler = $null
        $client = $null
        $live = $null
        $legacy = $null
        $ready = $null
        try {
            Add-Type -AssemblyName System.Net.Http
            if (-not ('SswDiagnosticHttpHandler' -as [type])) {
                Add-Type -TypeDefinition @'
using System.Net.Http;
public sealed class SswDiagnosticHttpHandler : HttpClientHandler
{
    public SswDiagnosticHttpHandler()
    {
        ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => true;
    }
}
'@ -ReferencedAssemblies 'System.Net.Http.dll'
            }
            $handler = New-Object SswDiagnosticHttpHandler
            $client = New-Object Net.Http.HttpClient($handler)
            $client.Timeout = [TimeSpan]::FromSeconds(5)
            $baseUri = "https://127.0.0.1:$([int]$config.Agent.Https.Port)"

            $live = $client.GetAsync("$baseUri/health/live").GetAwaiter().GetResult()
            if ([int]$live.StatusCode -eq 404) {
                $legacy = $client.GetAsync("$baseUri/health").GetAwaiter().GetResult()
                $result.agentLive = if ($legacy.IsSuccessStatusCode) { 'OK_LEGACY' } else { "AGENT_HTTP_$([int]$legacy.StatusCode)" }
                $result.agentReady = 'READINESS_ENDPOINT_UNAVAILABLE'
            }
            else {
                $result.agentLive = if ($live.IsSuccessStatusCode) { 'OK' } else { "AGENT_HTTP_$([int]$live.StatusCode)" }
                $ready = $client.GetAsync("$baseUri/health/ready").GetAwaiter().GetResult()
                if ($ready.IsSuccessStatusCode) {
                    $result.agentReady = 'OK'
                }
                else {
                    $fallbackCode = "AGENT_NOT_READY_$([int]$ready.StatusCode)"
                    try {
                        $readyBody = $ready.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                        $result.agentReady = if ($readyBody.code) { [string]$readyBody.code } else { $fallbackCode }
                    }
                    catch { $result.agentReady = $fallbackCode }
                }
            }
            $result.agentHttps = $result.agentLive
        }
        catch {
            $result.agentLive = 'AGENT_HTTPS_UNREACHABLE'
            $result.agentReady = 'AGENT_HTTPS_UNREACHABLE'
            $result.agentHttps = 'AGENT_HTTPS_UNREACHABLE'
        }
        finally {
            if ($ready) { $ready.Dispose() }
            if ($legacy) { $legacy.Dispose() }
            if ($live) { $live.Dispose() }
            if ($client) { $client.Dispose() }
            if ($handler) { $handler.Dispose() }
        }
    }
    catch { $result.configuration = 'CONFIG_INVALID' }
}

$json = $result | ConvertTo-Json
$json
if ($OutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, (New-Object Text.UTF8Encoding($false)))
    Write-SswStep '민감한 원문을 포함하지 않는 진단 JSON 저장 완료'
}
