param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Agent"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$install = [IO.Path]::GetFullPath($InstallDirectory)
$exe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
$configurationPath = Join-Path $install 'appsettings.Production.json'
$ownerSid = Get-SswCurrentUserSid
$mutex = $null
$exitCode = 1
$statusLogPath = $null

function Write-SswBackgroundLifecycle {
    param([Parameter(Mandatory = $true)][string]$Code, [string]$Detail)

    if ([string]::IsNullOrWhiteSpace($statusLogPath)) { return }
    try {
        if ((Test-Path -LiteralPath $statusLogPath -PathType Leaf) -and
            (Get-Item -LiteralPath $statusLogPath).Length -ge 1MB) {
            $previous = "$statusLogPath.1"
            if (Test-Path -LiteralPath $previous -PathType Leaf) { Remove-Item -LiteralPath $previous -Force }
            Move-Item -LiteralPath $statusLogPath -Destination $previous
        }
        $suffix = if ([string]::IsNullOrWhiteSpace($Detail)) { '' } else { " $Detail" }
        $line = "{0} {1}{2}{3}" -f [DateTimeOffset]::UtcNow.ToString('O'), $Code, $suffix, [Environment]::NewLine
        [IO.File]::AppendAllText($statusLogPath, $line, (New-Object Text.UTF8Encoding($false)))
    }
    catch {
        # 수명주기 로그 실패가 모니터링 프로세스를 중단시키지 않게 합니다.
    }
}

try {
    Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Agent'
    if (-not $install.Equals([IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw '숨김 실행기는 설치된 Agent 폴더에서만 실행할 수 있습니다.'
    }
    if (Get-Service -Name (Get-SswAgentServiceName) -ErrorAction SilentlyContinue) {
        throw 'Windows 서비스 방식과 현재 사용자 숨김 방식은 동시에 실행할 수 없습니다.'
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Agent 실행 파일을 찾지 못했습니다: $exe"
    }
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "현재 사용자 Agent 설정을 찾지 못했습니다: $configurationPath"
    }

    try { $configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "현재 사용자 Agent 설정을 읽지 못했습니다: $($_.Exception.Message)" }
    $dataDirectory = [IO.Path]::GetFullPath([string]$configuration.Agent.DataDirectory)
    Assert-SswProductPath -Path $dataDirectory -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'SamsungSwitchWatch\AgentData'
    $statusLogPath = Join-Path $dataDirectory 'background-runner.log'

    $createdNew = $false
    $mutexName = "Global\SamsungSwitchWatchAgent-CurrentUser-$ownerSid"
    $mutex = [Threading.Mutex]::new($false, $mutexName, [ref]$createdNew)
    if (-not $createdNew) {
        # 동일 사용자의 다른 로그인 세션에서 이미 실행 중이면 중복 Agent를 만들지 않습니다.
        Write-SswBackgroundLifecycle -Code 'RUNNER_ALREADY_ACTIVE'
        exit 0
    }

    Write-SswBackgroundLifecycle -Code 'RUNNER_START'
    $previousEnvironment = $env:DOTNET_ENVIRONMENT
    $env:DOTNET_ENVIRONMENT = 'Production'
    Push-Location -LiteralPath $install
    try {
        Write-SswBackgroundLifecycle -Code 'AGENT_START'
        & $exe
        $exitCode = if ($null -eq $LASTEXITCODE) { 1 } else { [int]$LASTEXITCODE }
        Write-SswBackgroundLifecycle -Code 'AGENT_EXIT' -Detail "code=$exitCode"
        # 정상 종료도 예약 작업 관점에서는 예기치 않은 종료이므로 복구 정책을 작동시킵니다.
        if ($exitCode -eq 0) { $exitCode = 1 }
    }
    catch {
        Write-SswBackgroundLifecycle -Code 'AGENT_START_FAILED'
        $exitCode = 1
    }
    finally {
        Pop-Location
        $env:DOTNET_ENVIRONMENT = $previousEnvironment
    }
}
catch {
    Write-SswBackgroundLifecycle -Code 'RUNNER_FAILED'
    $exitCode = 1
}
finally {
    if ($mutex) {
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
    }
}

exit $exitCode
