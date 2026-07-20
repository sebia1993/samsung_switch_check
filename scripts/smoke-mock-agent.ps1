param(
    [ValidateRange(1024, 65535)][int]$Port = 18543
)

. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentDirectory = Join-Path $repoRoot 'artifacts\publish\Agent'
$agentExe = Join-Path $agentDirectory 'SamsungSwitchWatch.Agent.exe'
$smokeDirectory = Join-Path $repoRoot 'artifacts\smoke-agent'
$stdout = Join-Path $smokeDirectory 'agent.stdout.log'
$stderr = Join-Path $smokeDirectory 'agent.stderr.log'
$baseUri = "http://127.0.0.1:$Port"
Assert-SswChildPath -Parent $repoRoot -Child $smokeDirectory
if (-not (Test-Path -LiteralPath $agentExe -PathType Leaf)) {
    throw '먼저 build-release.ps1을 실행하세요.'
}
if (Test-Path -LiteralPath $smokeDirectory) { Remove-Item -LiteralPath $smokeDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $smokeDirectory | Out-Null

$oldListen = $env:Agent__ListenUrl
$oldData = $env:Agent__DataDirectory
$oldPolling = $env:Agent__EnablePolling
$env:Agent__ListenUrl = $baseUri
$env:Agent__DataDirectory = $smokeDirectory
$env:Agent__EnablePolling = 'false'
$process = $null

try {
    $process = Start-Process -FilePath $agentExe -WorkingDirectory $agentDirectory -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "$baseUri/health" -TimeoutSec 2
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw '모의 Agent가 제한 시간 안에 시작되지 않았습니다.' }

    $pairing = Invoke-RestMethod -Uri "$baseUri/api/v1/pairing/bootstrap" -Method Post `
        -ContentType 'application/json' -Body '{}'
    $exchangeBody = @{ code = $pairing.code } | ConvertTo-Json -Compress
    $exchange = Invoke-RestMethod -Uri "$baseUri/api/v1/pairing/exchange" -Method Post `
        -ContentType 'application/json' -Body $exchangeBody
    $headers = @{ Authorization = ('Bearer {0}' -f $exchange.token) }

    $status = Invoke-RestMethod -Uri "$baseUri/api/v1/status" -Headers $headers
    $devices = @(Invoke-RestMethod -Uri "$baseUri/api/v1/devices" -Headers $headers)
    $command = Invoke-RestMethod -Uri "$baseUri/api/v1/commands/TEST-SW-01/interface_status" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
    $null = Invoke-RestMethod -Uri "$baseUri/api/dev/simulate/TEST-SW-01/down" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
    $events = @(Invoke-RestMethod -Uri "$baseUri/api/v1/events?after=0" -Headers $headers)
    $event = $events | Select-Object -Last 1
    if (-not $event) { throw '모의 이벤트가 생성되지 않았습니다.' }
    $acknowledged = Invoke-RestMethod -Uri ("$baseUri/api/v1/events/{0}/ack" -f $event.id) `
        -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'

    $apiJson = @($status, $devices, $command, $events, $acknowledged) | ConvertTo-Json -Depth 20
    if ($apiJson -match 'rawOutput') { throw 'API 응답에서 금지된 원문 필드가 발견되었습니다.' }

    [pscustomobject]@{
        Health = $health.status
        Mode = $health.mode
        DeviceCount = $status.deviceCount
        DevicesReturned = $devices.Count
        EventsReturned = $events.Count
        AcknowledgedState = $acknowledged.state
        CommandStatus = $command.collectorStatus
        RawLeakDetected = $false
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    $env:Agent__ListenUrl = $oldListen
    $env:Agent__DataDirectory = $oldData
    $env:Agent__EnablePolling = $oldPolling
    Remove-Variable exchange, headers -ErrorAction SilentlyContinue
}
