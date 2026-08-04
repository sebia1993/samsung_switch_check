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

    if ($lines.Length -ge 1 -and
        $lines[0] -ceq 'SSW_FIELD_DIAGNOSTIC/2') {
        return ConvertFrom-SswFieldDiagnosticV2 -Lines $lines
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

function Assert-SswAllowedToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string[]]$Allowed
    )

    if ($Allowed -cnotcontains $Value) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
}

function Assert-SswCompactDuration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -ceq 'unknown') {
        return
    }

    Assert-SswIntegerRange -Value $Value -Minimum 0 -Maximum 86400000
}

function Assert-SswProductVersionToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string[]]$UnavailableTokens
    )

    if ($UnavailableTokens -ccontains $Value) {
        return
    }

    if ($Value -cnotmatch (
            '^(?=.{1,64}$)' +
            '[0-9]{1,10}\.[0-9]{1,10}\.[0-9]{1,10}' +
            '(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?$')) {
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

    $legacyFixedKeys = @(
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
    $healthExtensionKeys = @(
        'AgentHealthCode',
        'AgentRestartObserved'
    )
    $failureExtensionKeys = @(
        'PrimaryFailureCode',
        'FailureCategory',
        'FailureStageDurationMs'
    )
    $transportObservationKeys = @(
        'ServiceRunningObserved',
        'ListenerOwnedObserved',
        'HttpAttemptCount',
        'LastTransportPhase'
    )
    $allowedFixedKeys = @(
        $legacyFixedKeys
        $healthExtensionKeys
        $failureExtensionKeys
        $transportObservationKeys
    )

    Assert-SswKeys -Values $Values -RequiredKeys $legacyFixedKeys -IsAllowed {
        param($key)
        return $allowedFixedKeys -ccontains $key -or
            $key -cmatch '^Stage\.[0-9]{2}\.(Code|Status|DurationMs|ElapsedMs)$'
    }

    $hasHealthExtension = @(
        $healthExtensionKeys |
            Where-Object { $Values.ContainsKey($_) }
    )
    $hasFailureExtension = @(
        $failureExtensionKeys |
            Where-Object { $Values.ContainsKey($_) }
    )
    $hasTransportObservation = @(
        $transportObservationKeys |
            Where-Object { $Values.ContainsKey($_) }
    )
    if ($hasHealthExtension.Count -notin @(0, $healthExtensionKeys.Count) -or
        $hasFailureExtension.Count -notin @(0, $failureExtensionKeys.Count) -or
        $hasTransportObservation.Count -notin @(
            0,
            $transportObservationKeys.Count
        ) -or
        ($hasFailureExtension.Count -gt 0 -and
         $hasHealthExtension.Count -eq 0) -or
        ($hasTransportObservation.Count -gt 0 -and
         $hasHealthExtension.Count -eq 0)) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
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
        -Pattern '^(PASS|PASS_OBSERVED|NOT_CONFIRMED|NOT_RUN)$'
    Assert-SswPattern -Values $Values -Key 'Readiness' `
        -Pattern '^(PASS|FAIL|NOT_RUN)$'

    if ($hasHealthExtension.Count -gt 0) {
        Assert-SswPattern -Values $Values -Key 'AgentHealthCode' `
            -Pattern '^[A-Z][A-Z0-9_]{1,63}$'
        Assert-SswPattern -Values $Values -Key 'AgentRestartObserved' `
            -Pattern '^(TRUE|FALSE)$'
    }

    if ($hasFailureExtension.Count -gt 0) {
        Assert-SswPattern -Values $Values -Key 'PrimaryFailureCode' `
            -Pattern '^[A-Z][A-Z0-9_]{1,63}$'
        Assert-SswPattern -Values $Values -Key 'FailureCategory' `
            -Pattern (
                '^(NOT_RUN|CLASSIFIED|ACCESS_DENIED|IO|TIMEOUT|' +
                'WINDOWS_API|INVALID_STATE|PLATFORM|UNKNOWN)$'
            )
        Assert-SswPattern -Values $Values -Key 'FailureStageDurationMs' `
            -Pattern '^(unknown|[0-9]{1,8})$'
    }

    if ($hasTransportObservation.Count -gt 0) {
        Assert-SswPattern -Values $Values -Key 'ServiceRunningObserved' `
            -Pattern '^(TRUE|FALSE)$'
        Assert-SswPattern -Values $Values -Key 'ListenerOwnedObserved' `
            -Pattern '^(TRUE|FALSE)$'
        Assert-SswIntegerRange -Value $Values.HttpAttemptCount `
            -Minimum 0 `
            -Maximum 10000
        Assert-SswPattern -Values $Values -Key 'LastTransportPhase' `
            -Pattern (
                '^(NOT_STARTED|LISTENER_OWNED|REQUEST_STARTED|' +
                'RESPONSE_HEADERS|RESPONSE_BODY|READINESS_VALIDATED)$'
            )
    }

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
    $expectedFixedKeyCount = $legacyFixedKeys.Count +
        $hasHealthExtension.Count +
        $hasFailureExtension.Count +
        $hasTransportObservation.Count
    if ($Values.Count -ne ($expectedFixedKeyCount + (4 * $stageCount))) {
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

function ConvertFrom-SswFieldDiagnosticV2 {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    if ($Lines.Length -lt 4 -or $Lines.Length -gt 12) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    foreach ($line in $Lines) {
        if ($line.Length -eq 0 -or $line.Length -gt 88) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }

    $payloadLines = $Lines[1..($Lines.Length - 1)]
    $payloadText = [string]::Join("`n", $payloadLines)
    if (Test-SswFieldDiagnosticContamination -Text $payloadText) {
        Stop-SswFieldDiagnostic -Code $script:InputError
    }

    $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase
    )
    $values = @{}
    foreach ($line in $payloadLines) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0 -or $separator -eq ($line.Length - 1)) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }

        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        if ($key -cne $key.Trim() -or
            $value -cne $value.Trim() -or
            $value.Contains(' ') -or
            $key -cnotmatch '^[A-Za-z][A-Za-z0-9]{0,31}$' -or
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

    if ($values.Component -ceq 'AGENT_SETUP') {
        Assert-SswAgentSetupV2Schema -Values $values
    }
    elseif ($values.Component -ceq 'VIEWER') {
        Assert-SswViewerV2Schema -Values $values
    }
    else {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    return [pscustomobject]@{
        Component = $values.Component
        ErrorCode = $values.ErrorCode
        FailedStage = $values.FailedStage
    }
}

function Get-SswAgentV2ErrorCodes {
    return @(
        'OK',
        'UNAVAILABLE',
        'SETUP_PACKAGE_NOT_FOUND',
        'SETUP_MANIFEST_INVALID',
        'SETUP_PACKAGE_HASH_MISMATCH',
        'SETUP_VIEWER_IP_INVALID',
        'SETUP_NETWORK_SELECTION_INVALID',
        'SETUP_EXISTING_NETWORKS_NOT_LOADED',
        'SETUP_ADMINISTRATOR_REQUIRED',
        'SETUP_PATH_INVALID',
        'SETUP_PATH_UNTRUSTED',
        'SETUP_PATH_NOT_WRITABLE',
        'SETUP_CONFIGURATION_INVALID',
        'SETUP_SERVICE_FAILED',
        'SETUP_FIREWALL_FAILED',
        'FIREWALL_REMOTE_ACCESS_UNCONFIRMED',
        'AGENT_LOCAL_CONNECTION_UNCONFIRMED',
        'SETUP_HEALTH_FAILED',
        'SETUP_ROLLBACK_FAILED',
        'SETUP_RECOVERY_REQUIRED',
        'ROLLBACK_STATE_MISMATCH',
        'ROLLBACK_SERVICE_STOP_FAILED',
        'ROLLBACK_FILE_RESTORE_FAILED',
        'ROLLBACK_DATA_CLEANUP_FAILED',
        'ROLLBACK_SERVICE_RESTORE_FAILED',
        'ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED',
        'ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED',
        'ROLLBACK_JOURNAL_WRITE_FAILED',
        'ROLLBACK_EVIDENCE_CLEANUP_FAILED',
        'ROLLBACK_STAGING_CLEANUP_FAILED',
        'ROLLBACK_BACKUP_CLEANUP_FAILED',
        'ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED',
        'ROLLBACK_JOURNAL_CLEANUP_FAILED',
        'SETUP_ALREADY_RUNNING',
        'SETUP_CANCELLED',
        'SETUP_UNEXPECTED',
        'DIAGNOSTIC_WRITE_FAILED'
    )
}

function Get-SswAgentV2StageCodes {
    return @(
        (Get-SswAgentV2ErrorCodes)
        'ADMINISTRATOR_OK',
        'INPUT_VALID',
        'PACKAGE_VALID',
        'PATHS_READY',
        'FIREWALL_OVERLAP_PROTECTED',
        'FIREWALL_GATE_READY',
        'SERVICE_FOUND',
        'SERVICE_NOT_INSTALLED',
        'FIREWALL_EXACT',
        'FIREWALL_UPDATE_REQUIRED',
        'FIREWALL_NOT_INSTALLED',
        'PACKAGE_STAGED',
        'SERVICE_CONFIGURED',
        'FIREWALL_CONFIGURED',
        'SERVICE_STARTED',
        'AGENT_READY',
        'AGENT_NOT_READY',
        'BACKUP_CLEANUP_PENDING',
        'JOURNAL_CLEANUP_PENDING',
        'RECOVERY_NOT_REQUIRED',
        'ROLLBACK_COMPLETED',
        'ROLLBACK_RECOVERY_CLEANED',
        'COMMITTED_TRANSACTION_CLEANED',
        'UNAVAILABLE'
    )
}

function Resolve-SswAgentV2Action {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    if ($ErrorCode -ceq 'OK') { return 'NONE' }
    if ($ErrorCode -cin @(
            'SETUP_PACKAGE_NOT_FOUND',
            'SETUP_MANIFEST_INVALID',
            'SETUP_PACKAGE_HASH_MISMATCH')) {
        return 'REPLACE_RELEASE_PACKAGE'
    }
    if ($ErrorCode -ceq 'SETUP_VIEWER_IP_INVALID') {
        return 'ENTER_VIEWER_FIXED_IPV4'
    }
    if ($ErrorCode -ceq 'SETUP_NETWORK_SELECTION_INVALID') {
        return 'SELECT_MANAGEMENT_NETWORK'
    }
    if ($ErrorCode -ceq 'SETUP_EXISTING_NETWORKS_NOT_LOADED') {
        return 'REVIEW_EXISTING_NETWORKS'
    }
    if ($ErrorCode -ceq 'SETUP_ADMINISTRATOR_REQUIRED') {
        return 'RUN_AS_ADMINISTRATOR'
    }
    if ($ErrorCode -cin @(
            'SETUP_PATH_INVALID',
            'SETUP_PATH_UNTRUSTED',
            'SETUP_PATH_NOT_WRITABLE')) {
        return 'CHECK_INSTALL_PERMISSIONS'
    }
    if ($ErrorCode -ceq 'SETUP_CONFIGURATION_INVALID') {
        return 'REVIEW_CONFIGURATION'
    }
    if ($ErrorCode -ceq 'SETUP_SERVICE_FAILED') {
        return 'CHECK_WINDOWS_SERVICE'
    }
    if ($ErrorCode -cin @(
            'SETUP_FIREWALL_FAILED',
            'FIREWALL_REMOTE_ACCESS_UNCONFIRMED')) {
        return 'CHECK_FIREWALL_POLICY'
    }
    if ($ErrorCode -cin @(
            'SETUP_HEALTH_FAILED',
            'AGENT_LOCAL_CONNECTION_UNCONFIRMED')) {
        return 'CHECK_AGENT_READINESS'
    }
    if ($ErrorCode -cin @(
            'SETUP_ROLLBACK_FAILED',
            'SETUP_RECOVERY_REQUIRED',
            'ROLLBACK_STATE_MISMATCH',
            'ROLLBACK_SERVICE_STOP_FAILED',
            'ROLLBACK_FILE_RESTORE_FAILED',
            'ROLLBACK_DATA_CLEANUP_FAILED',
            'ROLLBACK_SERVICE_RESTORE_FAILED',
            'ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED',
            'ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED',
            'ROLLBACK_JOURNAL_WRITE_FAILED',
            'ROLLBACK_EVIDENCE_CLEANUP_FAILED',
            'ROLLBACK_STAGING_CLEANUP_FAILED',
            'ROLLBACK_BACKUP_CLEANUP_FAILED',
            'ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED',
            'ROLLBACK_JOURNAL_CLEANUP_FAILED')) {
        return 'RUN_OR_REVIEW_RECOVERY'
    }
    if ($ErrorCode -ceq 'SETUP_ALREADY_RUNNING') { return 'WAIT_AND_RETRY' }
    if ($ErrorCode -ceq 'SETUP_CANCELLED') { return 'RETRY_WHEN_READY' }
    if ($ErrorCode -ceq 'DIAGNOSTIC_WRITE_FAILED') {
        return 'CHOOSE_WRITABLE_LOCATION'
    }

    return 'COLLECT_DIAGNOSTIC'
}

function Assert-SswAgentSetupV2Schema {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values
    )

    $keys = @(
        'Component',
        'ProductVersion',
        'Environment',
        'Run',
        'FailedStage',
        'ErrorCode',
        'Failure',
        'Action',
        'State',
        'Health',
        'Stages'
    )
    Assert-SswKeys -Values $Values -RequiredKeys $keys -IsAllowed {
        param($key)
        return $keys -ccontains $key
    }
    if ($Values.Count -ne $keys.Count) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    Assert-SswProductVersionToken -Value $Values.ProductVersion `
        -UnavailableTokens @('UNAVAILABLE')
    if ($Values.Environment -cnotmatch (
            '^[0-9]{8}T[0-9]{9}Z\|' +
            '(?:UNAVAILABLE|WIN_[0-9]+_[0-9]+_[0-9]+_[0-9]+)\|' +
            '(?:X86|X64|ARM|ARM64|UNAVAILABLE)$')) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    $run = $Values.Run -csplit '\|'
    if ($run.Count -ne 3) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswAllowedToken -Value $run[0] `
        -Allowed @('PREFLIGHT', 'INSTALL', 'RECOVERY', 'UNAVAILABLE')
    Assert-SswAllowedToken -Value $run[1] -Allowed @('SUCCESS', 'FAILURE')
    Assert-SswCompactDuration -Value $run[2]

    $failureStages = @(
        'NONE',
        'OPERATION_LOCK',
        'ADMINISTRATOR',
        'RECOVERY_JOURNAL',
        'INPUT',
        'PACKAGE_VALIDATION',
        'FILESYSTEM',
        'CONFIGURATION',
        'FILE_STAGING',
        'SERVICE_STOP',
        'FILE_ACTIVATION',
        'SERVICE_CONFIGURATION',
        'FIREWALL',
        'SERVICE_START',
        'READINESS',
        'COMMIT_CLEANUP',
        'RECOVERY',
        'UI_OPERATION',
        'UNKNOWN'
    )
    $errorCodes = @(Get-SswAgentV2ErrorCodes)
    Assert-SswAllowedToken -Value $Values.FailedStage -Allowed $failureStages
    Assert-SswAllowedToken -Value $Values.ErrorCode -Allowed $errorCodes

    $failure = $Values.Failure -csplit '\|'
    if ($failure.Count -ne 3) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswAllowedToken -Value $failure[0] `
        -Allowed (@('NONE') + $errorCodes)
    Assert-SswAllowedToken -Value $failure[1] -Allowed @(
        'NOT_RUN',
        'CLASSIFIED',
        'ACCESS_DENIED',
        'IO',
        'TIMEOUT',
        'WINDOWS_API',
        'INVALID_STATE',
        'PLATFORM',
        'UNKNOWN'
    )
    Assert-SswCompactDuration -Value $failure[2]

    if ($Values.Action -cne (Resolve-SswAgentV2Action $Values.ErrorCode)) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    $state = $Values.State -csplit '\|'
    if ($state.Count -ne 6) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswAllowedToken -Value $state[0] -Allowed @('PASS', 'FAIL', 'NOT_RUN')
    Assert-SswAllowedToken -Value $state[1] `
        -Allowed @('NONE', 'PENDING_RECOVERABLE', 'PENDING_BLOCKED')
    Assert-SswAllowedToken -Value $state[2] -Allowed @(
        'FAIL',
        'RUNNING_READY',
        'CONFIGURED',
        'NOT_INSTALLED',
        'FOUND',
        'RUNNING',
        'STOPPED',
        'UNKNOWN'
    )
    Assert-SswAllowedToken -Value $state[3] -Allowed @(
        'NONE',
        'READY',
        'EXACT',
        'PROTECTED',
        'UPDATE',
        'NOT_INSTALLED',
        'CONFIGURED',
        'NOT_CONFIRMED',
        'FAIL'
    )
    Assert-SswAllowedToken -Value $state[4] `
        -Allowed @('PASS', 'PASS_OBSERVED', 'NOT_CONFIRMED', 'NOT_RUN')
    Assert-SswAllowedToken -Value $state[5] `
        -Allowed @('PASS', 'FAIL', 'NOT_CONFIRMED', 'NOT_RUN')

    $health = $Values.Health -csplit '\|'
    if ($health.Count -ne 4) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswAllowedToken -Value $health[0] -Allowed @(
        'NOT_RUN',
        'READY',
        'SERVICEUNAVAILABLE',
        'SERVICEINSPECTIONFAILED',
        'TCPNOTLISTENING',
        'TCPOWNEDBYOTHERPROCESS',
        'TCPOWNERSHIPQUERYFAILED',
        'HTTPS_REQUEST_FAILED',
        'HTTPSTATUSINVALID',
        'PAYLOADTOOLARGE',
        'PAYLOADINVALID',
        'APIVERSIONMISMATCH',
        'PROTOCOLMISMATCH',
        'PRODUCTVERSIONMISMATCH',
        'DEADLINEEXCEEDED',
        'HTTPS_TLS_FAILED',
        'HTTPS_REQUEST_TIMEOUT',
        'HTTPS_CONNECTION_RESET',
        'HTTPS_EOF',
        'HTTPS_CONNECT_FAILED'
    )
    if ($health[1] -cnotmatch '^[TF]{3}$') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
    Assert-SswIntegerRange -Value $health[2] -Minimum 0 -Maximum 10000
    Assert-SswAllowedToken -Value $health[3] -Allowed @(
        'NOT_STARTED',
        'LISTENER_OWNED',
        'REQUEST_STARTED',
        'RESPONSE_HEADERS',
        'RESPONSE_BODY',
        'READINESS_VALIDATED'
    )

    $stages = $Values.Stages -csplit '\|'
    if ($stages.Count -ne 2) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswIntegerRange -Value $stages[0] -Minimum 0 -Maximum 64
    $stageCount = [int]$stages[0]
    if ($stageCount -eq 0) {
        if ($stages[1] -cne 'NONE:N') {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }
    else {
        $tail = $stages[1] -csplit '>'
        if ($tail.Count -lt 1 -or $tail.Count -gt $stageCount) {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
        $stageCodes = @(Get-SswAgentV2StageCodes)
        foreach ($entry in $tail) {
            $parts = $entry -csplit ':'
            if ($parts.Count -ne 2 -or $parts[0] -ceq 'NONE') {
                Stop-SswFieldDiagnostic -Code $script:SchemaError
            }
            Assert-SswAllowedToken -Value $parts[0] -Allowed $stageCodes
            Assert-SswAllowedToken -Value $parts[1] -Allowed @('S', 'W', 'F', 'R', 'N')
        }
    }

    if ($run[1] -ceq 'SUCCESS') {
        if ($Values.ErrorCode -cnotin @(
                'OK',
                'AGENT_LOCAL_CONNECTION_UNCONFIRMED',
                'FIREWALL_REMOTE_ACCESS_UNCONFIRMED') -or
            $Values.FailedStage -cne 'NONE' -or
            $failure[0] -cne 'NONE' -or
            $failure[1] -cne 'NOT_RUN' -or
            $failure[2] -cne 'unknown') {
            Stop-SswFieldDiagnostic -Code $script:SchemaError
        }
    }
    elseif ($Values.ErrorCode -ceq 'OK' -or
        $Values.FailedStage -ceq 'NONE' -or
        $failure[0] -ceq 'NONE' -or
        $failure[1] -ceq 'NOT_RUN') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
}

function Get-SswViewerV2ErrorCodes {
    return @(
        'NONE',
        'AGENT_ACCESS_DENIED',
        'AGENT_CLIENT_NOT_ALLOWED',
        'AGENT_CONNECTION_REFUSED',
        'AGENT_DNS_FAILED',
        'AGENT_HTTP_ERROR',
        'AGENT_IDENTITY_CHANGED',
        'AGENT_INTERNAL_ERROR',
        'AGENT_NOT_READY',
        'AGENT_PROTOCOL_MISMATCH',
        'AGENT_RESPONSE_INVALID',
        'AGENT_TIMEOUT',
        'AGENT_UNREACHABLE',
        'AGENT_VERSION_MISMATCH',
        'LOCAL_AGENT_PREFLIGHT_FAILED',
        'LOCAL_AGENT_PREFLIGHT_TIMEOUT',
        'LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED',
        'LOCAL_PRIVATE_IPV4_NOT_FOUND',
        'VIEWER_CONFIGURATION_INVALID',
        'VIEWER_CONNECTION_REQUIRED',
        'VIEWER_SETTINGS_WRITE_FAILED',
        'VIEWER_UNEXPECTED_ERROR'
    )
}

function Resolve-SswViewerV2Action {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ErrorCode
    )

    if ($ErrorCode -ceq 'NONE') { return 'NONE' }
    if ($ErrorCode -ceq 'AGENT_DNS_FAILED') { return 'CHECK_AGENT_ADDRESS_DNS' }
    if ($ErrorCode -cin @(
            'AGENT_CONNECTION_REFUSED',
            'AGENT_NOT_READY',
            'LOCAL_AGENT_PREFLIGHT_FAILED')) {
        return 'CHECK_AGENT_SERVICE'
    }
    if ($ErrorCode -cin @(
            'AGENT_TIMEOUT',
            'AGENT_UNREACHABLE',
            'LOCAL_AGENT_PREFLIGHT_TIMEOUT')) {
        return 'CHECK_NETWORK_FIREWALL'
    }
    if ($ErrorCode -cin @('AGENT_ACCESS_DENIED', 'AGENT_CLIENT_NOT_ALLOWED')) {
        return 'CHECK_ALLOWED_VIEWER_IP'
    }
    if ($ErrorCode -cin @(
            'AGENT_PROTOCOL_MISMATCH',
            'AGENT_VERSION_MISMATCH',
            'AGENT_RESPONSE_INVALID')) {
        return 'USE_MATCHING_RELEASE'
    }
    if ($ErrorCode -ceq 'AGENT_IDENTITY_CHANGED') {
        return 'VERIFY_AGENT_REPLACEMENT'
    }
    if ($ErrorCode -cin @(
            'LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED',
            'LOCAL_PRIVATE_IPV4_NOT_FOUND')) {
        return 'CHECK_LOCAL_NETWORK_ADAPTER'
    }
    if ($ErrorCode -ceq 'VIEWER_SETTINGS_WRITE_FAILED') {
        return 'CHECK_VIEWER_STORAGE'
    }
    if ($ErrorCode -cin @(
            'VIEWER_CONFIGURATION_INVALID',
            'VIEWER_CONNECTION_REQUIRED')) {
        return 'CHECK_VIEWER_CONNECTION_SETTINGS'
    }

    return 'CHECK_AGENT_DIAGNOSTIC'
}

function Assert-SswViewerV2Schema {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Values
    )

    $keys = @(
        'Component',
        'ProductVersion',
        'Environment',
        'Run',
        'FailedStage',
        'ErrorCode',
        'Action',
        'Stages',
        'TimingMs',
        'Agent'
    )
    Assert-SswKeys -Values $Values -RequiredKeys $keys -IsAllowed {
        param($key)
        return $keys -ccontains $key
    }
    if ($Values.Count -ne $keys.Count) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    Assert-SswProductVersionToken -Value $Values.ProductVersion `
        -UnavailableTokens @('UNKNOWN', 'UNAVAILABLE')
    if ($Values.Environment -cnotmatch (
            '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:' +
            '[0-9]{2}\.[0-9]{7}(?:Z|[+-][0-9]{2}:[0-9]{2})\|' +
            '(?:UNKNOWN|[0-9]{1,6})\|' +
            '(?:X86|X64|ARM|ARM64|UNKNOWN)$')) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    $run = $Values.Run -csplit '\|'
    if ($run.Count -ne 3) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswAllowedToken -Value $run[0] -Allowed @('NORMAL', 'SAME_PC')
    if ($run[1] -cne 'AGENT_CONNECTION_CHECK') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }
    Assert-SswAllowedToken -Value $run[2] -Allowed @('SUCCESS', 'FAILED')

    $failedStages = @(
        'NONE',
        'ADDRESS',
        'DNS',
        'TCP',
        'HTTPS',
        'IDENTITY',
        'SETTINGS',
        'UNKNOWN'
    )
    $errorCodes = @(Get-SswViewerV2ErrorCodes)
    Assert-SswAllowedToken -Value $Values.FailedStage -Allowed $failedStages
    Assert-SswAllowedToken -Value $Values.ErrorCode -Allowed $errorCodes
    if ($Values.Action -cne (Resolve-SswViewerV2Action $Values.ErrorCode)) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    if ($Values.Stages -cnotmatch (
            '^ADDR:(OK|FAIL|SKIP|PENDING)\|' +
            'DNS:(OK|FAIL|SKIP|PENDING)\|' +
            'TCP:(OK|FAIL|SKIP|PENDING)\|' +
            'HTTPS:(OK|FAIL|SKIP|PENDING)\|' +
            'ID:(OK|FAIL|SKIP|PENDING)$')) {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    $timings = $Values.TimingMs -csplit '\|'
    if ($timings.Count -ne 5) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    foreach ($timing in $timings) {
        Assert-SswIntegerRange -Value $timing -Minimum 0 -Maximum 300000
    }

    $agent = $Values.Agent -csplit '\|'
    if ($agent.Count -ne 3) { Stop-SswFieldDiagnostic -Code $script:SchemaError }
    Assert-SswIntegerRange -Value $agent[0] -Minimum 0 -Maximum 6
    Assert-SswProductVersionToken -Value $agent[1] `
        -UnavailableTokens @('UNKNOWN', 'UNAVAILABLE')
    if ($agent[2] -cnotmatch '^(UNKNOWN|[0-9]{1,4})$') {
        Stop-SswFieldDiagnostic -Code $script:SchemaError
    }

    if ($run[2] -ceq 'SUCCESS') {
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
            'AgentDeploymentOrchestratorTests.DeployAsync_FirewallVerificationTimeoutKeepsReadyAgentAndWarns'
        'AGENT_SETUP|SETUP_HEALTH_FAILED|READINESS' =
            'AgentDeploymentOrchestratorTests.DeployAsync_HealthFailureRestoresUpgradeFilesServiceFirewallAndIdentity'
        'AGENT_SETUP|AGENT_LOCAL_CONNECTION_UNCONFIRMED|NONE' =
            'AgentDeploymentOrchestratorTests.DeployAsync_AutomaticRequest_HealthFailureKeepsInstalledServiceAndWarns'
        'AGENT_SETUP|FIREWALL_REMOTE_ACCESS_UNCONFIRMED|NONE' =
            'AgentDeploymentOrchestratorTests.DeployAsync_AutomaticRequest_FirewallFailureKeepsInstalledServiceAndWarns'
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
            'AgentConnectionProbeTests.ProbeAsync_ProductVersionMismatchConnectsWithWarningWhenApiV4IsCompatible'
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
