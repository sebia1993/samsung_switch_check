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
        [ValidateSet(
            'Legacy',
            'Health',
            'Current',
            'HealthObserved',
            'CurrentObserved'
        )]
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
    if ($SchemaVariant -cin @('Current', 'CurrentObserved')) {
        $lines += @(
            ('PrimaryFailureCode=' + $ErrorCode),
            'FailureCategory=CLASSIFIED',
            'FailureStageDurationMs=unknown'
        )
    }

    $hasTransportObservation = $SchemaVariant -cin @(
        'HealthObserved',
        'CurrentObserved'
    )
    $lines += @(
        'RecommendedActionCode=CHECK_FIREWALL_POLICY',
        'OperationDurationMs=1200',
        'PackageValidation=PASS',
        'RecoveryJournal=NONE',
        $(if ($hasTransportObservation) {
            'Service=CONFIGURED'
        }
        else {
            'Service=NOT_INSTALLED'
        }),
        ('FirewallDecisionCodes=' + $ErrorCode),
        $(if ($hasTransportObservation) {
            'LocalTcp18443=PASS_OBSERVED'
        }
        else {
            'LocalTcp18443=NOT_RUN'
        }),
        $(if ($hasTransportObservation) {
            'Readiness=FAIL'
        }
        else {
            'Readiness=NOT_RUN'
        })
    )
    if ($SchemaVariant -cin @(
            'Health',
            'Current',
            'HealthObserved',
            'CurrentObserved'
        )) {
        $lines += @(
            $(if ($hasTransportObservation) {
                'AgentHealthCode=HTTPS_CONNECTION_RESET'
            }
            else {
                'AgentHealthCode=NOT_RUN'
            }),
            'AgentRestartObserved=FALSE'
        )
    }
    if ($hasTransportObservation) {
        $lines += @(
            'ServiceRunningObserved=TRUE',
            'ListenerOwnedObserved=TRUE',
            'HttpAttemptCount=3',
            'LastTransportPhase=REQUEST_STARTED'
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

function New-SswViewerV2Diagnostic {
    return @(
        'SSW_FIELD_DIAGNOSTIC/2',
        'Component=VIEWER',
        'ProductVersion=0.10.14-poc',
        'Environment=1970-01-01T00:00:00.0000000+00:00|UNKNOWN|UNKNOWN',
        'Run=NORMAL|AGENT_CONNECTION_CHECK|FAILED',
        'FailedStage=TCP',
        'ErrorCode=AGENT_CONNECTION_REFUSED',
        'Action=CHECK_AGENT_SERVICE',
        'Stages=ADDR:PENDING|DNS:PENDING|TCP:FAIL|HTTPS:SKIP|ID:SKIP',
        'TimingMs=0|0|3|0|0',
        'Agent=1|UNKNOWN|UNKNOWN'
    ) -join "`r`n"
}

function New-SswAgentSetupV2Diagnostic {
    return @(
        'SSW_FIELD_DIAGNOSTIC/2',
        'Component=AGENT_SETUP',
        'ProductVersion=0.10.14-poc',
        'Environment=20260804T010203000Z|WIN_10_0_26100_0|X64',
        'Run=INSTALL|FAILURE|64182',
        'FailedStage=READINESS',
        'ErrorCode=SETUP_HEALTH_FAILED',
        'Failure=SETUP_HEALTH_FAILED|CLASSIFIED|62011',
        'Action=CHECK_AGENT_READINESS',
        'State=PASS|NONE|CONFIGURED|CONFIGURED|PASS_OBSERVED|FAIL',
        'Health=HTTPS_REQUEST_TIMEOUT|FTT|3|REQUEST_STARTED',
        'Stages=7|SERVICE_STARTED:S>SETUP_HEALTH_FAILED:F>ROLLBACK_COMPLETED:S'
    ) -join "`r`n"
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

function Assert-SswReplayRejected {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedCode,
        [bool]$Bom = $true
    )

    $fixture = Write-SswFixture `
        -Name $Name `
        -Content $Content `
        -Bom $Bom
    $result = Invoke-SswReplay -FixturePath $fixture
    Assert-SswEqual -Expected 1 -Actual $result.ExitCode `
        -Message ($Name + ' must be rejected.')
    Assert-SswEqual -Expected $ExpectedCode -Actual $result.Output `
        -Message ($Name + ' returned the wrong stable rejection code.')
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
        'AgentDeploymentOrchestratorTests.DeployAsync_FirewallVerificationTimeoutKeepsReadyAgentAndWarns'
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

    $healthScenario =
        'AgentDeploymentOrchestratorTests.DeployAsync_HealthFailureRestoresUpgradeFilesServiceFirewallAndIdentity'
    $agentObservedDiagnostic = New-SswAgentSetupDiagnostic `
        -ErrorCode 'SETUP_HEALTH_FAILED' `
        -FailedStage 'READINESS' `
        -SchemaVariant 'CurrentObserved'
    $agentObservedFixture = Write-SswFixture `
        -Name 'agent-current-observed-valid.txt' `
        -Bom $true `
        -Content $agentObservedDiagnostic
    $agentObservedResult = Invoke-SswReplay -FixturePath $agentObservedFixture
    Assert-SswEqual -Expected 0 -Actual $agentObservedResult.ExitCode `
        -Message 'Agent Setup transport-observation input must succeed.'
    Assert-SswEqual -Expected $healthScenario -Actual $agentObservedResult.Output `
        -Message 'Transport observations selected the wrong fake scenario.'

    $agentHealthObservedFixture = Write-SswFixture `
        -Name 'agent-health-observed-valid.txt' `
        -Bom $true `
        -Content (
            New-SswAgentSetupDiagnostic `
                -ErrorCode 'SETUP_HEALTH_FAILED' `
                -FailedStage 'READINESS' `
                -SchemaVariant 'HealthObserved'
        )
    $agentHealthObservedResult = Invoke-SswReplay `
        -FixturePath $agentHealthObservedFixture
    Assert-SswEqual -Expected 0 -Actual $agentHealthObservedResult.ExitCode `
        -Message 'Health-only transport-observation input must succeed.'
    Assert-SswEqual `
        -Expected $healthScenario `
        -Actual $agentHealthObservedResult.Output `
        -Message 'Health-only observations selected the wrong fake scenario.'

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
    Assert-SswEqual `
        -Expected 32 `
        -Actual ([Regex]::Split($agentObservedDiagnostic, '\r\n').Count) `
        -Message 'Observed Agent Setup v1 fixture schema changed unexpectedly.'

    $viewerV2Diagnostic = New-SswViewerV2Diagnostic
    $viewerV2Lines = [Regex]::Split($viewerV2Diagnostic, '\r\n')
    Assert-SswEqual -Expected 11 -Actual $viewerV2Lines.Count `
        -Message 'Viewer v2 must remain a compact 11-line diagnostic.'
    Assert-SswTrue `
        -Condition (-not ($viewerV2Lines | Where-Object { $_.Length -gt 88 })) `
        -Message 'Viewer v2 lines must not exceed 88 characters.'
    $viewerV2Fixture = Write-SswFixture `
        -Name 'viewer-v2-valid.txt' `
        -Bom $true `
        -Content $viewerV2Diagnostic
    $viewerV2Result = Invoke-SswReplay -FixturePath $viewerV2Fixture
    Assert-SswEqual -Expected 0 -Actual $viewerV2Result.ExitCode `
        -Message 'Valid Viewer v2 input must succeed.'
    Assert-SswEqual -Expected $viewerScenario -Actual $viewerV2Result.Output `
        -Message 'Viewer v2 selected the wrong existing fake scenario.'

    $agentV2Diagnostic = New-SswAgentSetupV2Diagnostic
    $agentV2Lines = [Regex]::Split($agentV2Diagnostic, '\r\n')
    Assert-SswEqual -Expected 12 -Actual $agentV2Lines.Count `
        -Message 'Agent Setup v2 must remain a compact 12-line diagnostic.'
    Assert-SswTrue `
        -Condition (-not ($agentV2Lines | Where-Object { $_.Length -gt 88 })) `
        -Message 'Agent Setup v2 lines must not exceed 88 characters.'
    $agentV2Fixture = Write-SswFixture `
        -Name 'agent-v2-valid.txt' `
        -Bom $true `
        -Content $agentV2Diagnostic
    $agentV2Result = Invoke-SswReplay -FixturePath $agentV2Fixture
    Assert-SswEqual -Expected 0 -Actual $agentV2Result.ExitCode `
        -Message (
            'Valid Agent Setup v2 input must succeed: ' +
            $agentV2Result.Output)
    Assert-SswEqual -Expected $healthScenario -Actual $agentV2Result.Output `
        -Message 'Agent Setup v2 selected the wrong existing fake scenario.'

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
    $validObservedAgent = New-SswAgentSetupDiagnostic `
        -ErrorCode 'SETUP_HEALTH_FAILED' `
        -FailedStage 'READINESS' `
        -SchemaVariant 'CurrentObserved'
    $contaminatedTransportFixture = Write-SswFixture `
        -Name 'transport-contaminated.txt' `
        -Content (
            $validObservedAgent -replace
                'LastTransportPhase=REQUEST_STARTED',
                'LastTransportPhase=C:\ProgramData\private'
        )
    $contaminatedTransportResult = Invoke-SswReplay `
        -FixturePath $contaminatedTransportFixture
    Assert-SswEqual -Expected 1 -Actual $contaminatedTransportResult.ExitCode `
        -Message 'Contaminated transport observation must be rejected.'
    Assert-SswEqual `
        -Expected 'FIELD_DIAGNOSTIC_INPUT_REJECTED' `
        -Actual $contaminatedTransportResult.Output `
        -Message 'Contaminated transport observation must return only a safe code.'

    Assert-SswReplayRejected `
        -Name 'viewer-v2-bomless.txt' `
        -Content $viewerV2Diagnostic `
        -Bom $false `
        -ExpectedCode 'FIELD_DIAGNOSTIC_BOM_REQUIRED'
    Assert-SswReplayRejected `
        -Name 'viewer-v2-contaminated.txt' `
        -Content (
            $viewerV2Diagnostic -replace
                'FailedStage=TCP',
                'FailedStage=C:\private') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_INPUT_REJECTED'
    Assert-SswReplayRejected `
        -Name 'viewer-v2-unknown-key.txt' `
        -Content (
            $viewerV2Diagnostic -replace
                'Agent=1\|UNKNOWN\|UNKNOWN',
                'Unknown=SAFE') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'viewer-v2-duplicate-key.txt' `
        -Content (
            $viewerV2Diagnostic +
            "`r`nErrorCode=AGENT_CONNECTION_REFUSED") `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'viewer-v2-timing-range.txt' `
        -Content (
            $viewerV2Diagnostic -replace
                'TimingMs=0\|0\|3\|0\|0',
                'TimingMs=0|0|300001|0|0') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'

    $overlongStages = 'Stages=' + ('A' * 82)
    Assert-SswEqual -Expected 89 -Actual $overlongStages.Length `
        -Message 'The overlong v2 fixture must cross the 88-character boundary.'
    Assert-SswReplayRejected `
        -Name 'agent-v2-overlong-line.txt' `
        -Content (
            $agentV2Diagnostic -replace
                'Stages=.*$',
                $overlongStages) `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'agent-v2-too-many-lines.txt' `
        -Content ($agentV2Diagnostic + "`r`nExtra=SAFE") `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'agent-v2-attempt-range.txt' `
        -Content (
            $agentV2Diagnostic -replace
                'Health=HTTPS_REQUEST_TIMEOUT\|FTT\|3\|REQUEST_STARTED',
                'Health=HTTPS_REQUEST_TIMEOUT|FTT|10001|REQUEST_STARTED') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'agent-v2-zero-stage-malformed.txt' `
        -Content (
            $agentV2Diagnostic -replace
                'Stages=.*$',
                'Stages=0|NONE') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
    Assert-SswReplayRejected `
        -Name 'agent-v2-unknown-stage-code.txt' `
        -Content (
            $agentV2Diagnostic -replace
                'Stages=.*$',
                'Stages=1|SYNTHETIC_STAGE:F') `
        -ExpectedCode 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'

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
        ($validObservedAgent -replace
            "`r`nServiceRunningObserved=TRUE", ''),
        ($validObservedAgent -replace
            "`r`nListenerOwnedObserved=TRUE", ''),
        ($validObservedAgent -replace
            "`r`nHttpAttemptCount=3", ''),
        ($validObservedAgent -replace
            "`r`nLastTransportPhase=REQUEST_STARTED", ''),
        ($validObservedAgent -replace
            'ServiceRunningObserved=TRUE',
            'ServiceRunningObserved=YES'),
        ($validObservedAgent -replace
            'ListenerOwnedObserved=TRUE',
            'ListenerOwnedObserved=UNKNOWN'),
        ($validObservedAgent -replace
            'HttpAttemptCount=3',
            'HttpAttemptCount=10001'),
        ($validObservedAgent -replace
            'LastTransportPhase=REQUEST_STARTED',
            'LastTransportPhase=TLS'),
        ($validAgent -replace
            "`r`nStageCount=1",
            (
                "`r`nServiceRunningObserved=FALSE" +
                "`r`nListenerOwnedObserved=FALSE" +
                "`r`nHttpAttemptCount=0" +
                "`r`nLastTransportPhase=NOT_STARTED" +
                "`r`nStageCount=1"
            )),
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
