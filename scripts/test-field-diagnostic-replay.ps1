Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SswEqual {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Expected,
        [Parameter(Mandatory = $true)]
        [object]$Actual,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Expected -cne $Actual) {
        throw $Message
    }
}

function Assert-SswTrue {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$replayScript = Join-Path $PSScriptRoot 'replay-field-diagnostic.ps1'
$windowsPowerShell = Join-Path $env:SystemRoot `
    'System32\WindowsPowerShell\v1.0\powershell.exe'

if (-not [IO.File]::Exists($replayScript)) {
    throw 'Replay script is missing.'
}
if (-not [IO.File]::Exists($windowsPowerShell)) {
    throw 'Windows PowerShell 5.1 is required for this contract test.'
}

$scriptText = [IO.File]::ReadAllText($replayScript)
$forbiddenOperations = @(
    'Invoke-WebRequest',
    'Invoke-RestMethod',
    'Test-NetConnection',
    'TcpClient',
    'HttpClient',
    'New-NetFirewallRule',
    'netsh',
    'telnet.exe',
    'ssh.exe',
    'Start-Process'
)
foreach ($operation in $forbiddenOperations) {
    Assert-SswTrue `
        -Condition (-not $scriptText.Contains($operation)) `
        -Message 'Replay script must remain file-only and offline.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ssw-field-diagnostic-' + [Guid]::NewGuid().ToString('N')
)
[void][IO.Directory]::CreateDirectory($tempRoot)

function Write-SswFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [bool]$Bom = $true
    )

    $fixturePath = Join-Path $tempRoot $Name
    $encoding = New-Object System.Text.UTF8Encoding($Bom)
    [IO.File]::WriteAllText($fixturePath, $Content, $encoding)
    return $fixturePath
}

function New-SswViewerDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorCode,
        [Parameter(Mandatory = $true)]
        [string]$FailedStage
    )

    return @(
        'SSW_FIELD_DIAGNOSTIC/1',
        'Component=VIEWER',
        'ProductVersion=0.10.8-poc',
        'GeneratedUtc=2026-07-29T01:02:03.0000000+00:00',
        'WindowsBuild=22631',
        'Architecture=X64',
        'Operation=AGENT_CONNECTION_CHECK',
        'Result=FAILED',
        ('FailedStage=' + $FailedStage),
        ('ErrorCode=' + $ErrorCode),
        'RecommendedActionCode=CHECK_AGENT_SERVICE',
        'Mode=NORMAL',
        'AddressStatus=SUCCEEDED',
        'AddressDurationMs=1',
        'DnsStatus=SUCCEEDED',
        'DnsDurationMs=2',
        'TcpStatus=FAILED',
        'TcpDurationMs=3',
        'HttpsStatus=NOT_RUN',
        'HttpsDurationMs=0',
        'IdentityStatus=NOT_RUN',
        'IdentityDurationMs=0',
        'CandidateCount=0',
        'AgentProductVersion=UNKNOWN',
        'ApiVersion=UNKNOWN'
    ) -join "`r`n"
}

function New-SswAgentSetupDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorCode,
        [Parameter(Mandatory = $true)]
        [string]$FailedStage,
        [ValidateSet('Legacy', 'Health', 'Current')]
        [string]$SchemaVariant = 'Legacy'
    )

    $lines = @(
        'SSW_FIELD_DIAGNOSTIC/1',
        'Component=AGENT_SETUP',
        'ProductVersion=0.10.8-poc',
        'GeneratedUtc=20260729T010203000Z',
        'WindowsBuild=WIN_10_0_22631_0',
        'Architecture=X64',
        'Operation=INSTALL',
        'Result=FAILURE',
        ('FailedStage=' + $FailedStage),
        ('ErrorCode=' + $ErrorCode)
    )
    if ($SchemaVariant -ceq 'Current') {
        $lines += @(
            ('PrimaryFailureCode=' + $ErrorCode),
            'FailureCategory=CLASSIFIED',
            'FailureStageDurationMs=unknown'
        )
    }

    $lines += @(
        'RecommendedActionCode=CHECK_FIREWALL_POLICY',
        'OperationDurationMs=1200',
        'PackageValidation=PASS',
        'RecoveryJournal=NONE',
        'Service=NOT_INSTALLED',
        ('FirewallDecisionCodes=' + $ErrorCode),
        'LocalTcp18443=NOT_RUN',
        'Readiness=NOT_RUN'
    )
    if ($SchemaVariant -cin @('Health', 'Current')) {
        $lines += @(
            'AgentHealthCode=NOT_RUN',
            'AgentRestartObserved=FALSE'
        )
    }

    $lines += @(
        'StageCount=1',
        ('Stage.01.Code=' + $ErrorCode),
        'Stage.01.Status=FAILURE',
        'Stage.01.DurationMs=100',
        'Stage.01.ElapsedMs=1200'
    )
    return $lines -join "`r`n"
}

function Invoke-SswReplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FixturePath
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $windowsPowerShell
    $startInfo.Arguments = (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Path "{1}"' -f
        $replayScript,
        $FixturePath
    )
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        Assert-SswTrue -Condition $process.Start() `
            -Message 'Failed to start the replay contract process.'
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            throw 'Replay contract process exceeded its deadline.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = (($stdout + $stderr).Trim())
    }
}

try {
    $viewerScenario =
        'AgentConnectionProbeTests.ProbeAsync_ConnectionRefusedIdentifiesTcpStage'
    $viewerFixture = Write-SswFixture `
        -Name 'viewer-valid.txt' `
        -Bom $true `
        -Content (
            New-SswViewerDiagnostic `
                -ErrorCode 'AGENT_CONNECTION_REFUSED' `
                -FailedStage 'TCP'
        )
    $viewerResult = Invoke-SswReplay -FixturePath $viewerFixture
    Assert-SswEqual -Expected 0 -Actual $viewerResult.ExitCode `
        -Message 'Valid BOM input must succeed.'
    Assert-SswEqual -Expected $viewerScenario -Actual $viewerResult.Output `
        -Message 'Viewer diagnostic selected the wrong existing fake scenario.'

    $agentScenario =
        'AgentDeploymentOrchestratorTests.DeployAsync_FirewallVerificationTimeoutRollsBackWithSanitizedMismatch'
    $agentFixture = Write-SswFixture `
        -Name 'agent-valid.txt' `
        -Bom $true `
        -Content (
            New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_FIREWALL_FAILED' `
                -FailedStage 'FIREWALL'
        )
    $agentResult = Invoke-SswReplay -FixturePath $agentFixture
    Assert-SswEqual -Expected 0 -Actual $agentResult.ExitCode `
        -Message 'Valid Agent Setup input must succeed.'
    Assert-SswEqual -Expected $agentScenario -Actual $agentResult.Output `
        -Message 'Agent Setup diagnostic selected the wrong existing fake scenario.'

    $agentHealthFixture = Write-SswFixture `
        -Name 'agent-health-valid.txt' `
        -Bom $true `
        -Content (
            New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_FIREWALL_FAILED' `
                -FailedStage 'FIREWALL' `
                -SchemaVariant 'Health'
        )
    $agentHealthResult = Invoke-SswReplay -FixturePath $agentHealthFixture
    Assert-SswEqual -Expected 0 -Actual $agentHealthResult.ExitCode `
        -Message 'Agent Setup v1 health-extension input must succeed.'
    Assert-SswEqual -Expected $agentScenario -Actual $agentHealthResult.Output `
        -Message 'Agent Setup health extension selected the wrong fake scenario.'

    $agentCurrentDiagnostic = New-SswAgentSetupDiagnostic `
        -ErrorCode 'SETUP_FIREWALL_FAILED' `
        -FailedStage 'FIREWALL' `
        -SchemaVariant 'Current'
    $agentCurrentFixture = Write-SswFixture `
        -Name 'agent-current-valid.txt' `
        -Bom $true `
        -Content $agentCurrentDiagnostic
    $agentCurrentResult = Invoke-SswReplay -FixturePath $agentCurrentFixture
    Assert-SswEqual -Expected 0 -Actual $agentCurrentResult.ExitCode `
        -Message 'Current Agent Setup v1 input must succeed.'
    Assert-SswEqual -Expected $agentScenario -Actual $agentCurrentResult.Output `
        -Message 'Current Agent Setup diagnostic selected the wrong fake scenario.'

    Assert-SswEqual `
        -Expected 23 `
        -Actual ([Regex]::Split(
            (New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_FIREWALL_FAILED' `
                -FailedStage 'FIREWALL'),
            '\r\n').Count) `
        -Message 'Legacy Agent Setup v1 fixture schema changed unexpectedly.'
    Assert-SswEqual `
        -Expected 28 `
        -Actual ([Regex]::Split($agentCurrentDiagnostic, '\r\n').Count) `
        -Message 'Current Agent Setup v1 fixture schema changed unexpectedly.'

    $settingsScenario =
        'ViewerSettingsTests.SaveCoordinator_SaveOrThrowPreservesFailClosedConnectionFlow'
    $settingsFixture = Write-SswFixture `
        -Name 'viewer-settings-valid.txt' `
        -Bom $true `
        -Content (
            New-SswViewerDiagnostic `
                -ErrorCode 'VIEWER_SETTINGS_WRITE_FAILED' `
                -FailedStage 'SETTINGS'
        )
    $settingsResult = Invoke-SswReplay -FixturePath $settingsFixture
    Assert-SswEqual -Expected 0 -Actual $settingsResult.ExitCode `
        -Message 'Valid Viewer settings diagnostic must succeed.'
    Assert-SswEqual -Expected $settingsScenario -Actual $settingsResult.Output `
        -Message 'Viewer settings diagnostic selected the wrong fake scenario.'

    $rollbackReadinessScenario =
        'AgentDeploymentOrchestratorTests.DeployAsync_RollbackServiceStopFailureBlocksFileAndServiceRestore'
    $rollbackReadinessFixture = Write-SswFixture `
        -Name 'agent-rollback-readiness-valid.txt' `
        -Bom $true `
        -Content (
            New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_ROLLBACK_FAILED' `
                -FailedStage 'READINESS'
        )
    $rollbackReadinessResult = Invoke-SswReplay `
        -FixturePath $rollbackReadinessFixture
    Assert-SswEqual -Expected 0 -Actual $rollbackReadinessResult.ExitCode `
        -Message 'Valid rollback readiness diagnostic must succeed.'
    Assert-SswEqual `
        -Expected $rollbackReadinessScenario `
        -Actual $rollbackReadinessResult.Output `
        -Message 'Rollback readiness selected the wrong fake scenario.'

    $rollbackRecoveryScenario =
        'AgentDeploymentOrchestratorTests.RecoverAsync_LegacyPendingRecoveryUpgradesJournalBeforeRollback'
    $rollbackRecoveryFixture = Write-SswFixture `
        -Name 'agent-rollback-recovery-valid.txt' `
        -Bom $true `
        -Content (
            New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_ROLLBACK_FAILED' `
                -FailedStage 'RECOVERY'
        )
    $rollbackRecoveryResult = Invoke-SswReplay `
        -FixturePath $rollbackRecoveryFixture
    Assert-SswEqual -Expected 0 -Actual $rollbackRecoveryResult.ExitCode `
        -Message 'Valid rollback recovery diagnostic must succeed.'
    Assert-SswEqual `
        -Expected $rollbackRecoveryScenario `
        -Actual $rollbackRecoveryResult.Output `
        -Message 'Rollback recovery selected the wrong fake scenario.'

    $scenarioSourceFiles = @{
        'AgentConnectionProbeTests' = Join-Path $repoRoot `
            'tests/SamsungSwitchWatch.Viewer.Tests/AgentConnectionProbeTests.cs'
        'AgentDeploymentOrchestratorTests' = Join-Path $repoRoot `
            'tests/SamsungSwitchWatch.Agent.Setup.Tests/AgentDeploymentOrchestratorTests.cs'
        'ViewerSettingsTests' = Join-Path $repoRoot `
            'tests/SamsungSwitchWatch.Viewer.Tests/ViewerSettingsTests.cs'
    }
    $scenarioNames = [Regex]::Matches(
        $scriptText,
        "'((?:AgentConnectionProbeTests|AgentDeploymentOrchestratorTests|" +
        "ViewerSettingsTests)" +
        "\.[A-Za-z0-9_]+)'"
    ) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    Assert-SswEqual -Expected 13 -Actual $scenarioNames.Count `
        -Message 'The replay scenario allowlist changed without a contract update.'
    foreach ($scenario in $scenarioNames) {
        $className = $scenario.Substring(0, $scenario.IndexOf('.'))
        $method = $scenario.Substring($scenario.IndexOf('.') + 1)
        $sourceText = [IO.File]::ReadAllText($scenarioSourceFiles[$className])
        Assert-SswTrue `
            -Condition (
                $sourceText -match (
                    '(?m)\b(?:Task|void)\s+' +
                    [Regex]::Escape($method) +
                    '\('
                )
            ) `
            -Message 'Replay output must name an existing fake-backed test method.'
    }

    $bomlessFixture = Write-SswFixture `
        -Name 'viewer-bomless.txt' `
        -Bom $false `
        -Content (
            New-SswViewerDiagnostic `
                -ErrorCode 'AGENT_CONNECTION_REFUSED' `
                -FailedStage 'TCP'
        )
    $bomlessResult = Invoke-SswReplay -FixturePath $bomlessFixture
    Assert-SswEqual -Expected 1 -Actual $bomlessResult.ExitCode `
        -Message 'BOM-less field diagnostic must be rejected.'
    Assert-SswEqual `
        -Expected 'FIELD_DIAGNOSTIC_BOM_REQUIRED' `
        -Actual $bomlessResult.Output `
        -Message 'BOM-less rejection must return only the stable BOM code.'

    $validViewer = New-SswViewerDiagnostic `
        -ErrorCode 'AGENT_CONNECTION_REFUSED' `
        -FailedStage 'TCP'
    $contaminatedPayloads = @(
        ($validViewer -replace 'FailedStage=TCP', 'FailedStage=192.0.2.10'),
        ($validViewer -replace 'FailedStage=TCP', 'FailedStage=192.0.2.0/24'),
        ($validViewer -replace 'FailedStage=TCP', 'FailedStage=C:\ProgramData\private'),
        ($validViewer -replace 'FailedStage=TCP', 'FailedStage=System.InvalidOperationException'),
        ($validViewer + "`r`nPassword=synthetic-secret")
    )
    $contaminatedIndex = 0
    foreach ($payload in $contaminatedPayloads) {
        $contaminatedIndex++
        $fixture = Write-SswFixture `
            -Name ('contaminated-{0}.txt' -f $contaminatedIndex) `
            -Content $payload
        $result = Invoke-SswReplay -FixturePath $fixture
        Assert-SswEqual -Expected 1 -Actual $result.ExitCode `
            -Message 'Contaminated input must be rejected.'
        Assert-SswEqual `
            -Expected 'FIELD_DIAGNOSTIC_INPUT_REJECTED' `
            -Actual $result.Output `
            -Message 'Contaminated input must return only a stable safe code.'
    }

    $validAgent = New-SswAgentSetupDiagnostic `
        -ErrorCode 'SETUP_FIREWALL_FAILED' `
        -FailedStage 'FIREWALL'
    $validCurrentAgent = New-SswAgentSetupDiagnostic `
        -ErrorCode 'SETUP_FIREWALL_FAILED' `
        -FailedStage 'FIREWALL' `
        -SchemaVariant 'Current'
    $schemaPayloads = @(
        ($validViewer -replace "`r`nFailedStage=TCP", ''),
        ($validViewer + "`r`nErrorCode=AGENT_TIMEOUT"),
        ($validViewer + "`r`nNote=SAFE_VALUE"),
        ($validViewer -replace 'ProductVersion=0.10.8-poc', 'ProductVersion=viewer-host'),
        ($validViewer -replace 'AgentProductVersion=UNKNOWN', 'AgentProductVersion=monitor-pc'),
        ($validAgent -replace 'ProductVersion=0.10.8-poc', 'ProductVersion=agent-host'),
        ($validAgent -replace "`r`nStage.01.ElapsedMs=1200", ''),
        ($validCurrentAgent -replace "`r`nPrimaryFailureCode=SETUP_FIREWALL_FAILED", ''),
        ($validCurrentAgent -replace "`r`nAgentRestartObserved=FALSE", ''),
        ($validCurrentAgent + "`r`nNote=SAFE_VALUE"),
        ($validAgent -replace
            "`r`nRecommendedActionCode=CHECK_FIREWALL_POLICY",
            (
                "`r`nPrimaryFailureCode=SETUP_FIREWALL_FAILED" +
                "`r`nRecommendedActionCode=CHECK_FIREWALL_POLICY"
            ))
    )
    $schemaIndex = 0
    foreach ($payload in $schemaPayloads) {
        $schemaIndex++
        $fixture = Write-SswFixture `
            -Name ('schema-{0}.txt' -f $schemaIndex) `
            -Content $payload
        $result = Invoke-SswReplay -FixturePath $fixture
        Assert-SswEqual -Expected 1 -Actual $result.ExitCode `
            -Message 'Invalid schema must be rejected.'
        Assert-SswEqual `
            -Expected 'FIELD_DIAGNOSTIC_SCHEMA_INVALID' `
            -Actual $result.Output `
            -Message 'Invalid schema must return only a stable safe code.'
    }

    $unsupportedFixture = Write-SswFixture `
        -Name 'unsupported.txt' `
        -Content (
            New-SswViewerDiagnostic `
                -ErrorCode 'AGENT_HTTP_ERROR' `
                -FailedStage 'TCP'
        )
    $unsupportedResult = Invoke-SswReplay -FixturePath $unsupportedFixture
    Assert-SswEqual -Expected 1 -Actual $unsupportedResult.ExitCode `
        -Message 'Unsupported scenario must fail.'
    Assert-SswEqual `
        -Expected 'FIELD_DIAGNOSTIC_SCENARIO_UNSUPPORTED' `
        -Actual $unsupportedResult.Output `
        -Message 'Unsupported scenario must return only a stable safe code.'
}
finally {
    if ([IO.Directory]::Exists($tempRoot)) {
        Get-ChildItem -LiteralPath $tempRoot -File |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Remove-Item -LiteralPath $tempRoot -Force
    }
}

Write-Host 'Field diagnostic replay contract checks passed.'
