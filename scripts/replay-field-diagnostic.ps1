[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SchemaError = 'FIELD_DIAGNOSTIC_SCHEMA_INVALID'
$script:InputError = 'FIELD_DIAGNOSTIC_INPUT_REJECTED'

function Stop-SswFieldDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    throw (New-Object System.IO.InvalidDataException($Code))
}

function Read-SswFieldDiagnosticText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputPath
    )

    if (-not [IO.File]::Exists($InputPath)) {
        Stop-SswFieldDiagnostic -Code 'FIELD_DIAGNOSTIC_FILE_NOT_FOUND'
    }

    try {
        $bytes = [IO.File]::ReadAllBytes($InputPath)
    }
    catch {
        Stop-SswFieldDiagnostic -Code 'FIELD_DIAGNOSTIC_READ_FAILED'
    }

    if ($bytes.Length -eq 0 -or $bytes.Length -gt 65536) {
        Stop-SswFieldDiagnostic -Code $script:InputError
    }

    if ($bytes.Length -lt 3 -or
        $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or
        $bytes[2] -ne 0xBF) {
        Stop-SswFieldDiagnostic -Code 'FIELD_DIAGNOSTIC_BOM_REQUIRED'
    }
    $offset = 3

    try {
        $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
        return $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch {
        Stop-SswFieldDiagnostic -Code 'FIELD_DIAGNOSTIC_UTF8_INVALID'
    }
}

function Test-SswFieldDiagnosticContamination {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $patterns = @(
        '(?i)\b(password|passwd|pwd|secret|token|credential|community|private[_ -]?key|thumbprint)\b',
        '(?<![0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?:/[0-9]{1,2})?(?![0-9])',
        '(?i)(?:[a-z]:[\\/]|\\\\[^\\\r\n]+\\|/(?:home|users|var|etc|tmp)/)',
        '(?i)\b(?:[a-z0-9_.]*exception|stack[ _-]?trace|inner[ _-]?exception)\b',
        '(?i)(?:^|\s)at\s+[a-z_][a-z0-9_.]*\s*\('
    )

    foreach ($pattern in $patterns) {
        if ($Text -match $pattern) {
            return $true
        }
    }

    return $false
}

function ConvertFrom-SswFieldDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    if ($Text.IndexOf([char]0) -ge 0 -or
        $Text -match '[\x01-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        Stop-SswFieldDiagnostic -Code $script:InputError
    }

    $lines = [Regex]::Split($Text, '\r?\n')
    if ($lines.Length -gt 0 -and $lines[$lines.Length - 1] -eq '') {
        $lines = $lines[0..($lines.Length - 2)]
    }

    if ($lines.Length -lt 4 -or
        $lines.Length -gt 400 -or
        $lines[0] -cne 'SSW_FIELD_DIAGNOSTIC/1') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    $payloadLines = $lines[1..($lines.Length - 1)]
    $payloadText = [string]::Join("`n", $payloadLines)
    if (Test-SswFieldDiagnosticContamination -Text $payloadText) {
        Stop-SswFieldDiagnostic -Code $script:InputError
    }

    $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase
    )
    $values = @{}

    foreach ($line in $payloadLines) {
        if ($line.Length -eq 0 -or $line.Length -gt 512) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }

        $separator = $line.IndexOf('=')
        if ($separator -le 0 -or $separator -eq ($line.Length - 1)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }

        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        if ($key -cne $key.Trim() -or $value -cne $value.Trim()) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }

        if ($key -cnotmatch '^[A-Za-z][A-Za-z0-9.]{0,63}$' -or
            -not $seenKeys.Add($key)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }

        $values[$key] = $value
    }

    foreach ($requiredKey in @('Component', 'ErrorCode', 'FailedStage')) {
        if (-not $values.ContainsKey($requiredKey)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }

    if ($values.Component -cnotmatch '^(AGENT_SETUP|VIEWER)$' -or
        $values.ErrorCode -cnotmatch '^[A-Z][A-Z0-9_]{1,63}$' -or
        $values.FailedStage -cnotmatch '^[A-Z][A-Z0-9_]{0,63}$') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    if ($values.Component -ceq 'AGENT_SETUP') {
        Assert-SswAgentSetupSchema -Values $values
    }
    else {
        Assert-SswViewerSchema -Values $values
    }

    return [pscustomobject]@{
        Component = $values.Component
        ErrorCode = $values.ErrorCode
        FailedStage = $values.FailedStage
    }
}

function Assert-SswKeys {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values,
        [Parameter(Mandatory = $true)]
        [string[]]$RequiredKeys,
        [Parameter(Mandatory = $true)]
        [scriptblock]$IsAllowed
    )

    foreach ($requiredKey in $RequiredKeys) {
        if (-not $Values.ContainsKey($requiredKey)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }

    foreach ($key in $Values.Keys) {
        if (-not (& $IsAllowed $key)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }
}

function Assert-SswPattern {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values,
        [Parameter(Mandatory = $true)]
        [string]$Key,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    if ($Values[$Key] -cnotmatch $Pattern) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
}

function Assert-SswIntegerRange {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [int64]$Minimum,
        [Parameter(Mandatory = $true)]
        [int64]$Maximum
    )

    [int64]$parsed = 0
    if (-not [int64]::TryParse(
            $Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -or
        $parsed -lt $Minimum -or
        $parsed -gt $Maximum) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
}

function Assert-SswCommonFieldValues {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values,
        [Parameter(Mandatory = $true)]
        [string]$GeneratedUtcPattern,
        [Parameter(Mandatory = $true)]
        [string]$WindowsBuildPattern
    )

    Assert-SswPattern -Values $Values -Key 'ProductVersion' `
        -Pattern (
            '^(?=.{1,64}$)(?:UNKNOWN|UNAVAILABLE|' +
            '[0-9]{1,10}\.[0-9]{1,10}\.[0-9]{1,10}' +
            '(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)$'
        )
    Assert-SswPattern -Values $Values -Key 'GeneratedUtc' `
        -Pattern $GeneratedUtcPattern
    Assert-SswPattern -Values $Values -Key 'WindowsBuild' `
        -Pattern $WindowsBuildPattern
    Assert-SswPattern -Values $Values -Key 'Architecture' `
        -Pattern '^(X86|X64|ARM|ARM64|UNKNOWN|UNAVAILABLE)$'
    Assert-SswPattern -Values $Values -Key 'Operation' `
        -Pattern '^[A-Z][A-Z0-9_]{0,63}$'
    Assert-SswPattern -Values $Values -Key 'Result' `
        -Pattern '^[A-Z][A-Z0-9_]{0,31}$'
    Assert-SswPattern -Values $Values -Key 'RecommendedActionCode' `
        -Pattern '^[A-Z][A-Z0-9_]{0,63}$'
}

function Assert-SswAgentSetupSchema {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values
    )

    $fixedKeys = @(
        'Component',
        'ProductVersion',
        'GeneratedUtc',
        'WindowsBuild',
        'Architecture',
        'Operation',
        'Result',
        'FailedStage',
        'ErrorCode',
        'RecommendedActionCode',
        'OperationDurationMs',
        'PackageValidation',
        'RecoveryJournal',
        'Service',
        'FirewallDecisionCodes',
        'LocalTcp18443',
        'Readiness',
        'StageCount'
    )

    Assert-SswKeys -Values $Values -RequiredKeys $fixedKeys -IsAllowed {
        param($key)
        return $fixedKeys -ccontains $key -or
            $key -cmatch '^Stage\.[0-9]{2}\.(Code|Status|DurationMs|ElapsedMs)$'
    }
    Assert-SswCommonFieldValues -Values $Values `
        -GeneratedUtcPattern '^[0-9]{8}T[0-9]{9}Z$' `
        -WindowsBuildPattern '^(UNAVAILABLE|WIN_[0-9]+_[0-9]+_[0-9]+_[0-9]+)$'
    Assert-SswPattern -Values $Values -Key 'Operation' `
        -Pattern '^(PREFLIGHT|INSTALL|RECOVERY|UNAVAILABLE)$'
    Assert-SswPattern -Values $Values -Key 'Result' `
        -Pattern '^(SUCCESS|FAILURE)$'
    Assert-SswPattern -Values $Values -Key 'OperationDurationMs' `
        -Pattern '^(unknown|[0-9]{1,8})$'
    Assert-SswPattern -Values $Values -Key 'PackageValidation' `
        -Pattern '^(PASS|FAIL|NOT_RUN)$'
    Assert-SswPattern -Values $Values -Key 'RecoveryJournal' `
        -Pattern '^(NONE|PENDING_RECOVERABLE|PENDING_BLOCKED)$'
    Assert-SswPattern -Values $Values -Key 'Service' `
        -Pattern '^(FAIL|RUNNING_READY|CONFIGURED|NOT_INSTALLED|FOUND|RUNNING|STOPPED|UNKNOWN)$'
    Assert-SswPattern -Values $Values -Key 'FirewallDecisionCodes' `
        -Pattern '^(NONE|[A-Z][A-Z0-9_]*(,[A-Z][A-Z0-9_]*)*)$'
    Assert-SswPattern -Values $Values -Key 'LocalTcp18443' `
        -Pattern '^(PASS|NOT_CONFIRMED|NOT_RUN)$'
    Assert-SswPattern -Values $Values -Key 'Readiness' `
        -Pattern '^(PASS|FAIL|NOT_RUN)$'

    if ($Values.Result -ceq 'SUCCESS') {
        if ($Values.ErrorCode -cne 'OK' -or $Values.FailedStage -cne 'NONE') {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }
    elseif ($Values.ErrorCode -ceq 'OK' -or $Values.FailedStage -ceq 'NONE') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    Assert-SswIntegerRange -Value $Values.StageCount -Minimum 0 -Maximum 64
    $stageCount = [int]$Values.StageCount
    if ($Values.Count -ne ($fixedKeys.Count + (4 * $stageCount))) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    for ($index = 1; $index -le $stageCount; $index++) {
        $prefix = 'Stage.{0:D2}' -f $index
        foreach ($suffix in @('Code', 'Status', 'DurationMs', 'ElapsedMs')) {
            if (-not $Values.ContainsKey($prefix + '.' + $suffix)) {
                Stop-SswFieldDiagnostic -Code $script:SchemaError
            }
        }

        Assert-SswPattern -Values $Values -Key ($prefix + '.Code') `
            -Pattern '^[A-Z][A-Z0-9_]{0,63}$'
        Assert-SswPattern -Values $Values -Key ($prefix + '.Status') `
            -Pattern '^(PENDING|RUNNING|SUCCESS|FAILURE|WARNING|INFORMATION|UNAVAILABLE)$'
        Assert-SswPattern -Values $Values -Key ($prefix + '.DurationMs') `
            -Pattern '^(unknown|[0-9]{1,8})$'
        Assert-SswPattern -Values $Values -Key ($prefix + '.ElapsedMs') `
            -Pattern '^(unknown|[0-9]{1,8})$'
    }
}

function Assert-SswViewerSchema {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values
    )

    $keys = @(
        'Component',
        'ProductVersion',
        'GeneratedUtc',
        'WindowsBuild',
        'Architecture',
        'Operation',
        'Result',
        'FailedStage',
        'ErrorCode',
        'RecommendedActionCode',
        'Mode',
        'AddressStatus',
        'AddressDurationMs',
        'DnsStatus',
        'DnsDurationMs',
        'TcpStatus',
        'TcpDurationMs',
        'HttpsStatus',
        'HttpsDurationMs',
        'IdentityStatus',
        'IdentityDurationMs',
        'CandidateCount',
        'AgentProductVersion',
        'ApiVersion'
    )

    Assert-SswKeys -Values $Values -RequiredKeys $keys -IsAllowed {
        param($key)
        return $keys -ccontains $key
    }
    if ($Values.Count -ne $keys.Count) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    Assert-SswCommonFieldValues -Values $Values `
        -GeneratedUtcPattern (
            '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:' +
            '[0-9]{2}\.[0-9]{7}(Z|[+-][0-9]{2}:[0-9]{2})$'
        ) `
        -WindowsBuildPattern '^(UNKNOWN|[0-9]{1,6})$'
    Assert-SswPattern -Values $Values -Key 'Operation' `
        -Pattern '^AGENT_CONNECTION_CHECK$'
    Assert-SswPattern -Values $Values -Key 'Result' `
        -Pattern '^(SUCCESS|FAILED)$'
    Assert-SswPattern -Values $Values -Key 'Mode' `
        -Pattern '^(NORMAL|SAME_PC)$'
    Assert-SswPattern -Values $Values -Key 'FailedStage' `
        -Pattern '^(NONE|ADDRESS|DNS|TCP|HTTPS|IDENTITY|SETTINGS|UNKNOWN)$'
    Assert-SswPattern -Values $Values -Key 'AgentProductVersion' `
        -Pattern (
            '^(?=.{1,64}$)(?:UNKNOWN|UNAVAILABLE|' +
            '[0-9]{1,10}\.[0-9]{1,10}\.[0-9]{1,10}' +
            '(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)$'
        )
    Assert-SswPattern -Values $Values -Key 'ApiVersion' `
        -Pattern '^(UNKNOWN|[0-9]{1,4})$'
    Assert-SswIntegerRange -Value $Values.CandidateCount -Minimum 0 -Maximum 6

    foreach ($stage in @('Address', 'Dns', 'Tcp', 'Https', 'Identity')) {
        Assert-SswPattern -Values $Values -Key ($stage + 'Status') `
            -Pattern '^(NOT_RUN|RUNNING|SUCCEEDED|FAILED)$'
        Assert-SswIntegerRange `
            -Value $Values[$stage + 'DurationMs'] `
            -Minimum 0 `
            -Maximum 300000
    }

    if ($Values.Result -ceq 'SUCCESS') {
        if ($Values.ErrorCode -cne 'NONE' -or $Values.FailedStage -cne 'NONE') {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }
    elseif ($Values.ErrorCode -ceq 'NONE' -or $Values.FailedStage -ceq 'NONE') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
}

function Resolve-SswFieldDiagnosticScenario {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Diagnostic
    )

    $scenarios = @{
        'AGENT_SETUP|SETUP_FIREWALL_FAILED|FIREWALL' =
            'AgentDeploymentOrchestratorTests.DeployAsync_FirewallVerificationTimeoutRollsBackWithSanitizedMismatch'
        'AGENT_SETUP|SETUP_HEALTH_FAILED|READINESS' =
            'AgentDeploymentOrchestratorTests.DeployAsync_HealthFailureRestoresUpgradeFilesServiceFirewallAndIdentity'
        'AGENT_SETUP|SETUP_RECOVERY_REQUIRED|RECOVERY_JOURNAL' =
            'AgentDeploymentOrchestratorTests.DeployAsync_RefusesPendingBackupUntilExplicitRecovery'
        'AGENT_SETUP|SETUP_ROLLBACK_FAILED|RECOVERY' =
            'AgentDeploymentOrchestratorTests.RecoverAsync_LegacyPendingRecoveryUpgradesJournalBeforeRollback'
        'AGENT_SETUP|SETUP_ROLLBACK_FAILED|READINESS' =
            'AgentDeploymentOrchestratorTests.DeployAsync_RollbackServiceStopFailureBlocksFileAndServiceRestore'
        'AGENT_SETUP|SETUP_UNEXPECTED|UNKNOWN' =
            'AgentDeploymentOrchestratorTests.RecoverAsync_UnexpectedWindowsFailureReturnsStableResult'
        'VIEWER|AGENT_DNS_FAILED|DNS' =
            'AgentConnectionProbeTests.ProbeAsync_DnsFailureStopsBeforeTcpAndUsesStableCode'
        'VIEWER|AGENT_CONNECTION_REFUSED|TCP' =
            'AgentConnectionProbeTests.ProbeAsync_ConnectionRefusedIdentifiesTcpStage'
        'VIEWER|AGENT_TIMEOUT|TCP' =
            'AgentConnectionProbeTests.ProbeAsync_TcpTimeoutUsesStableTimeoutCode'
        'VIEWER|AGENT_IDENTITY_CHANGED|HTTPS' =
            'AgentConnectionProbeTests.ProbeAsync_TlsIdentityFailureIsReportedAtHttpsStage'
        'VIEWER|AGENT_RESPONSE_INVALID|IDENTITY' =
            'AgentConnectionProbeTests.ProbeAsync_InvalidApiAfterTlsIsReportedAtIdentityStage'
        'VIEWER|AGENT_VERSION_MISMATCH|IDENTITY' =
            'AgentConnectionProbeTests.ProbeAsync_ProductVersionMismatchFailsClosedAtIdentityStage'
        'VIEWER|VIEWER_SETTINGS_WRITE_FAILED|SETTINGS' =
            'ViewerSettingsTests.SaveCoordinator_SaveOrThrowPreservesFailClosedConnectionFlow'
    }

    $key = '{0}|{1}|{2}' -f @(
        $Diagnostic.Component,
        $Diagnostic.ErrorCode,
        $Diagnostic.FailedStage
    )
    if (-not $scenarios.ContainsKey($key)) {
        Stop-SswFieldDiagnostic -Code 'FIELD_DIAGNOSTIC_SCENARIO_UNSUPPORTED'
    }

    return $scenarios[$key]
}

try {
    $text = Read-SswFieldDiagnosticText -InputPath $Path
    $diagnostic = ConvertFrom-SswFieldDiagnostic -Text $text
    $scenario = Resolve-SswFieldDiagnosticScenario -Diagnostic $diagnostic
    [Console]::Out.WriteLine($scenario)
    exit 0
}
catch {
    $safeCode = $_.Exception.Message
    if ($safeCode -cnotmatch '^FIELD_DIAGNOSTIC_[A-Z_]+$') {
        $safeCode = 'FIELD_DIAGNOSTIC_REPLAY_FAILED'
    }

    [Console]::Error.WriteLine($safeCode)
    exit 1
}
