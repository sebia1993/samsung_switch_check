param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateRange(5, 60)][int]$ProcessTimeoutSeconds = 20
)

. (Join-Path $PSScriptRoot 'common.ps1')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}

$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release -PathType Container)) {
    throw "Release directory is missing: $release"
}

$agentZip = Join-Path $release "SamsungSwitchWatch-Agent-$Version-win-x64.zip"
$viewerZip = Join-Path $release "SamsungSwitchWatch-Viewer-$Version-win-x64.zip"
foreach ($zipPath in @($agentZip, $viewerZip)) {
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Release ZIP is missing: $(Split-Path -Leaf $zipPath)"
    }
}
if (-not (Test-SswAdministrator)) {
    throw 'AGENT_SETUP_PACKAGE_SMOKE_REQUIRES_ELEVATION'
}

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$smokeRoot = Join-Path $temporaryParent (
    'SamsungSwitchWatch-release-executable-smoke-' +
    [Guid]::NewGuid().ToString('N'))
Assert-SswChildPath -Parent $temporaryParent -Child $smokeRoot

$agentDirectory = Join-Path $smokeRoot 'Agent'
$viewerDirectory = Join-Path $smokeRoot 'Viewer'
$runtimeProbeDirectory = Join-Path $smokeRoot 'empty-runtime'
$agentDataDirectory = Join-Path $smokeRoot 'agent-data'
$agentStdout = Join-Path $smokeRoot 'agent.stdout.log'
$agentStderr = Join-Path $smokeRoot 'agent.stderr.log'

$environmentNames = @(
    'PATH',
    'DOTNET_ROOT',
    'DOTNET_ROOT_X64',
    'DOTNET_MULTILEVEL_LOOKUP',
    'HTTP_PROXY',
    'HTTPS_PROXY',
    'ALL_PROXY',
    'NO_PROXY',
    'Agent__ListenUrl',
    'Agent__DataDirectory',
    'Agent__MockMode',
    'Agent__AllowedTargetCidrs__0'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] =
        [Environment]::GetEnvironmentVariable($name, 'Process')
}

$agentProcess = $null

function Invoke-SswBoundedExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$Argument,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$FailurePrefix
    )

    $process = $null
    try {
        try {
            $process = Start-Process -FilePath $FilePath -ArgumentList $Argument `
                -WorkingDirectory $WorkingDirectory -WindowStyle Hidden `
                -PassThru -ErrorAction Stop
        }
        catch {
            throw "$FailurePrefix`_START_FAILED"
        }
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            catch { }
            throw "$FailurePrefix`_TIMEOUT"
        }
        if ($process.ExitCode -ne 0) {
            throw "$FailurePrefix`_EXITED_NONZERO: $($process.ExitCode)"
        }
    }
    finally {
        if ($process) {
            if (-not $process.HasExited) {
                try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
                catch { }
                try { $process.WaitForExit(5000) | Out-Null }
                catch { }
            }
            $process.Dispose()
        }
    }
}

try {
    New-Item -ItemType Directory -Path $agentDirectory, $viewerDirectory, `
        $runtimeProbeDirectory, $agentDataDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $agentZip -DestinationPath $agentDirectory -Force
    Expand-Archive -LiteralPath $viewerZip -DestinationPath $viewerDirectory -Force

    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $env:DOTNET_ROOT = $runtimeProbeDirectory
    $env:DOTNET_ROOT_X64 = $runtimeProbeDirectory
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:HTTP_PROXY = 'http://127.0.0.1:1'
    $env:HTTPS_PROXY = 'http://127.0.0.1:1'
    $env:ALL_PROXY = 'http://127.0.0.1:1'
    $env:NO_PROXY = '127.0.0.1,localhost'

    Write-SswStep 'Viewer Setup ZIP executable smoke'
    Invoke-SswBoundedExecutable `
        -FilePath (Join-Path $viewerDirectory 'SamsungSwitchWatch.Viewer.Setup.exe') `
        -Argument '--package-smoke-check' `
        -WorkingDirectory $viewerDirectory `
        -FailurePrefix 'VIEWER_SETUP_PACKAGE_SMOKE'

    Write-SswStep 'Viewer runtime ZIP executable smoke'
    Invoke-SswBoundedExecutable `
        -FilePath (Join-Path $viewerDirectory 'SamsungSwitchWatch.Viewer.exe') `
        -Argument '--install-smoke-check' `
        -WorkingDirectory $viewerDirectory `
        -FailurePrefix 'VIEWER_PACKAGE_SMOKE'

    Write-SswStep 'Agent Setup ZIP executable smoke'
    Invoke-SswBoundedExecutable `
        -FilePath (Join-Path $agentDirectory 'SamsungSwitchWatch.Agent.Setup.exe') `
        -Argument '--package-smoke-check' `
        -WorkingDirectory $agentDirectory `
        -FailurePrefix 'AGENT_SETUP_PACKAGE_SMOKE'

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }

    $baseUri = "http://127.0.0.1:$port"
    $env:Agent__ListenUrl = $baseUri
    $env:Agent__DataDirectory = $agentDataDirectory
    $env:Agent__MockMode = 'true'
    $env:Agent__AllowedTargetCidrs__0 = '10.40.0.0/16'

    Write-SswStep 'Agent ZIP MockMode executable smoke'
    $agentExecutable =
        Join-Path $agentDirectory 'SamsungSwitchWatch.Agent.exe'
    $agentProcess = Start-Process -FilePath $agentExecutable `
        -ArgumentList '--service' -WorkingDirectory $agentDirectory `
        -WindowStyle Hidden -RedirectStandardOutput $agentStdout `
        -RedirectStandardError $agentStderr -PassThru -ErrorAction Stop

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ProcessTimeoutSeconds)
    $health = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($agentProcess.HasExited) {
            throw "AGENT_PACKAGE_SMOKE_EXITED_EARLY: $($agentProcess.ExitCode)"
        }
        try {
            $candidate = Invoke-RestMethod -Uri "$baseUri/health/ready" `
                -TimeoutSec 1
            if ($candidate.status -eq 'ready') {
                $health = $candidate
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if ($null -eq $health) {
        throw 'AGENT_PACKAGE_SMOKE_READY_TIMEOUT'
    }

    $identity = Invoke-RestMethod -Uri "$baseUri/api/v4/identity" -TimeoutSec 3
    if ($identity.apiVersion -ne 4 -or $identity.protocol -ne 'https') {
        throw 'AGENT_PACKAGE_SMOKE_IDENTITY_INVALID'
    }

    $commonRequest = [ordered]@{
        requestId = 'release-package-smoke'
        host = '10.40.0.10'
        port = 23
        model = 'IES4224GP'
        username = 'synthetic-user'
        password = 'synthetic-password'
        enablePassword = $null
    }
    $testRequest = [ordered]@{} + $commonRequest
    $testRequest.purpose = 'test'
    $testRequest.commands = @()
    $testResult = Invoke-RestMethod -Uri "$baseUri/api/v4/telnet/test" `
        -Method Post -ContentType 'application/json' `
        -Body ($testRequest | ConvertTo-Json -Depth 5 -Compress) -TimeoutSec 3
    if (-not $testResult.success -or @($testResult.commands).Count -ne 0) {
        throw 'AGENT_PACKAGE_SMOKE_TEST_INVALID'
    }

    $executeRequest = [ordered]@{} + $commonRequest
    $executeRequest.requestId = 'release-package-smoke-query'
    $executeRequest.purpose = 'manual'
    $executeRequest.commands = @('show port status')
    $executeResult = Invoke-RestMethod -Uri "$baseUri/api/v4/telnet/execute" `
        -Method Post -ContentType 'application/json' `
        -Body ($executeRequest | ConvertTo-Json -Depth 5 -Compress) -TimeoutSec 3
    if (-not $executeResult.success -or
        @($executeResult.commands).Count -ne 1 -or
        $executeResult.commands[0].command -ne 'show port status' -or
        $executeResult.commands[0].output -ne 'Synthetic mock Telnet output.') {
        throw 'AGENT_PACKAGE_SMOKE_QUERY_INVALID'
    }

    Write-SswStep 'Release executable smoke passed'
}
finally {
    if ($agentProcess) {
        if (-not $agentProcess.HasExited) {
            try {
                Stop-Process -Id $agentProcess.Id -Force `
                    -ErrorAction SilentlyContinue
            }
            catch { }
            try { $agentProcess.WaitForExit(5000) | Out-Null }
            catch { }
        }
        $agentProcess.Dispose()
    }

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousEnvironment[$name],
            'Process')
    }

    if (Test-Path -LiteralPath $smokeRoot) {
        Assert-SswChildPath -Parent $temporaryParent -Child $smokeRoot
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}
