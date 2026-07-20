param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$ServiceName = 'SamsungSwitchWatchAgent',
    [string]$OutputPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

$install = [IO.Path]::GetFullPath($InstallDirectory)
$configPath = Join-Path $install 'appsettings.Production.json'
$result = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    service = 'SERVICE_NOT_FOUND'
    configuration = 'CONFIG_NOT_FOUND'
    certificate = 'CERT_NOT_FOUND'
    database = 'DATABASE_NOT_FOUND'
    switchTcp = 'NOT_TESTED'
    agentHttps = 'NOT_TESTED'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
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
        $handler = $null
        $client = $null
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

        try {
            Add-Type -AssemblyName System.Net.Http
            if (-not ('SswLocalDiagnosticHttpHandler' -as [type])) {
                Add-Type -TypeDefinition @'
using System.Net.Http;
public sealed class SswLocalDiagnosticHttpHandler : HttpClientHandler
{
    public SswLocalDiagnosticHttpHandler()
    {
        ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => true;
    }
}
'@ -ReferencedAssemblies 'System.Net.Http.dll'
            }
            $handler = New-Object SswLocalDiagnosticHttpHandler
            $client = New-Object Net.Http.HttpClient($handler)
            $client.Timeout = [TimeSpan]::FromSeconds(5)
            $health = $client.GetAsync("https://127.0.0.1:$([int]$config.Agent.Https.Port)/health").GetAwaiter().GetResult()
            $result.agentHttps = if ($health.IsSuccessStatusCode) { 'OK' } else { "AGENT_HTTP_$([int]$health.StatusCode)" }
        }
        catch { $result.agentHttps = 'AGENT_HTTPS_UNREACHABLE' }
        finally {
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
