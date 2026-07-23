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
if (Test-Path -LiteralPath $smokeDirectory) {
    Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $smokeDirectory | Out-Null

$environmentNames = @(
    'Agent__ListenUrl',
    'Agent__DataDirectory',
    'Agent__MockMode',
    'Agent__AllowedTargetCidrs__0'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$env:Agent__ListenUrl = $baseUri
$env:Agent__DataDirectory = $smokeDirectory
$env:Agent__MockMode = 'true'
$env:Agent__AllowedTargetCidrs__0 = '10.40.0.0/16'
$process = $null

try {
    $process = Start-Process -FilePath $agentExe -ArgumentList '--service' `
        -WorkingDirectory $agentDirectory -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "$baseUri/health/ready" -TimeoutSec 2
            $ready = $health.status -eq 'ready'
            if ($ready) { break }
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) {
        throw '모의 Agent가 제한 시간 안에 시작되지 않았습니다.'
    }

    $identity = Invoke-RestMethod -Uri "$baseUri/api/v4/identity" -TimeoutSec 5
    $commonRequest = [ordered]@{
        requestId = 'smoke-test'
        host = '10.40.0.10'
        port = 23
        model = 'IES4224GP'
        username = 'mock-user'
        password = 'mock-password'
        enablePassword = $null
    }
    $testRequest = [ordered]@{} + $commonRequest
    $testRequest.purpose = 'test'
    $testRequest.commands = @()
    $testResult = Invoke-RestMethod -Uri "$baseUri/api/v4/telnet/test" `
        -Method Post -ContentType 'application/json' `
        -Body ($testRequest | ConvertTo-Json -Depth 5 -Compress) -TimeoutSec 5

    $executeRequest = [ordered]@{} + $commonRequest
    $executeRequest.requestId = 'smoke-execute'
    $executeRequest.purpose = 'manual'
    $executeRequest.commands = @('show port status')
    $executeResult = Invoke-RestMethod -Uri "$baseUri/api/v4/telnet/execute" `
        -Method Post -ContentType 'application/json' `
        -Body ($executeRequest | ConvertTo-Json -Depth 5 -Compress) -TimeoutSec 5

    if ($identity.apiVersion -ne 4 -or $identity.protocol -ne 'https') {
        throw 'Agent identity 계약이 v4 HTTPS 실행기와 일치하지 않습니다.'
    }
    if (-not $testResult.success -or @($testResult.commands).Count -ne 0) {
        throw '모의 Telnet 접속 시험 응답이 올바르지 않습니다.'
    }
    if (-not $executeResult.success -or @($executeResult.commands).Count -ne 1 -or
        $executeResult.commands[0].command -ne 'show port status' -or
        $executeResult.commands[0].output -ne 'Synthetic mock Telnet output.') {
        throw '모의 Telnet 명령 실행 응답이 올바르지 않습니다.'
    }

    [pscustomobject]@{
        Health = $health.status
        ApiVersion = $identity.apiVersion
        AgentId = $identity.agentId
        TestSucceeded = $testResult.success
        ExecuteSucceeded = $executeResult.success
        CommandCount = @($executeResult.commands).Count
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            [string]$previousEnvironment[$name],
            'Process')
    }
}
