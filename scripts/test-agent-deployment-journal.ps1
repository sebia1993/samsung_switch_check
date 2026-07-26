param(
    [switch]$RequireElevatedAclFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-JournalTest {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Assert-JournalFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Code
    )

    $failure = $null
    try { & $Action }
    catch { $failure = $_.Exception.Message }
    Assert-JournalTest -Condition ([string]$failure -like "${Code}:*") `
        -Message "Expected $Code but received: $failure"
}

function Get-TestTreeFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return '<missing>' }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $entries = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $root -Recurse -Force | Sort-Object FullName)) {
        $relative = $item.FullName.Substring($root.Length + 1)
        if ($item.PSIsContainer) {
            $entries += "D|$relative"
        }
        else {
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            $entries += "F|$relative|$($item.Length)|$hash"
        }
    }
    return $entries -join "`n"
}

function Assert-GuardFailurePreservesTree {
    param(
        [Parameter(Mandatory = $true)][string]$OperationsRoot,
        [string]$FingerprintRoot = $OperationsRoot,
        [Parameter(Mandatory = $true)][string]$Code
    )
    $before = Get-TestTreeFingerprint -Path $FingerprintRoot
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Assert-JournalFailure -Code $Code -Action {
            Assert-SswAgentDeploymentJournalsReady -OperationsRoot $OperationsRoot
        }
        $after = Get-TestTreeFingerprint -Path $FingerprintRoot
        Assert-JournalTest -Condition ($after -ceq $before) `
            -Message "Guard failure $Code changed the fixture tree on attempt $attempt."
    }
}

function Write-TestJournal {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Status,
        [string[]]$ErrorCodes = @(),
        [int]$FormatVersion = 1,
        [string]$Product = 'SamsungSwitchWatch',
        [string]$TransactionId = ([Guid]::NewGuid().ToString('N')),
        [string]$UpdatedUtc = ([DateTimeOffset]::UtcNow.ToString('O'))
    )

    $payload = [ordered]@{
        formatVersion = $FormatVersion
        product = $Product
        operation = $Operation
        transactionId = $TransactionId
        stage = $Stage
        status = $Status
        version = 'test'
        updatedUtc = $UpdatedUtc
        errorCodes = @($ErrorCodes)
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($Path, $payload, (New-Object Text.UTF8Encoding($false)))
}

function Reset-TestRoot {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $Path
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

$testId = [Guid]::NewGuid().ToString('N')
$folderName = [string]::Concat(
    'SamsungSwitchWatch ',
    [char]0xC791,
    [char]0xC5C5,
    ' ',
    [char]0xAE30,
    [char]0xB85D,
    ' ',
    $testId)
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) $folderName
$testRoot = Join-Path $fixtureRoot 'operations'
$programRoot = Join-Path $fixtureRoot 'program'
$installJournal = Join-Path $testRoot 'agent-install-or-update.json'
$uninstallJournal = Join-Path $testRoot 'agent-uninstall.json'
$sentinel = Join-Path $testRoot 'unrelated.keep'

try {
    Write-SswStep 'Agent deployment journal fail-closed contract'

    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot

    Reset-TestRoot -Path $testRoot
    [IO.File]::WriteAllText($sentinel, 'keep')
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'completed' -Status 'succeeded'
    Write-TestJournal -Path $uninstallJournal -Operation 'agent-uninstall' `
        -Stage 'completed' -Status 'succeeded'
    $completedLeftover = Join-Path $testRoot 'transactions\22222222222222222222222222222222'
    New-Item -ItemType Directory -Path $completedLeftover -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $completedLeftover 'cleanup.pending'), 'preserve')
    $completedBefore = Get-TestTreeFingerprint -Path $testRoot
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot
    Assert-JournalTest -Condition (
        (Get-TestTreeFingerprint -Path $testRoot) -ceq $completedBefore
    ) -Message 'Completed journal checks must not clean transaction leftovers.'
    Assert-JournalTest -Condition ((Get-Content -LiteralPath $sentinel -Raw) -eq 'keep') `
        -Message 'Completed journal checks must not modify unrelated files.'

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'rollback-completed' -Status 'failed'
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'prepared' -Status 'running'
    $partialTransaction = Join-Path $testRoot 'transactions\11111111111111111111111111111111'
    $partialSnapshot = Join-Path $partialTransaction 'data'
    $partialStaging = Join-Path $programRoot 'Agent.__staging_11111111111111111111111111111111'
    $partialBackup = Join-Path $programRoot 'Agent.__backup_11111111111111111111111111111111'
    New-Item -ItemType Directory -Path $partialSnapshot, $partialStaging, $partialBackup -Force |
        Out-Null
    [IO.File]::WriteAllText((Join-Path $partialSnapshot 'identity.partial'), 'snapshot')
    [IO.File]::WriteAllText((Join-Path $partialStaging 'agent.partial'), 'staging')
    [IO.File]::WriteAllText((Join-Path $partialBackup 'agent.old'), 'backup')
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -FingerprintRoot $fixtureRoot `
        -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED'

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'completed' -Status 'succeeded'
    Write-TestJournal -Path $uninstallJournal -Operation 'agent-uninstall' `
        -Stage 'prepared' -Status 'running' `
        -UpdatedUtc ([DateTimeOffset]::UtcNow.AddYears(5).ToString('O'))
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED'

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'rollback-completed' -Status 'failed' -ErrorCodes @('RESTORE_PROGRAM_FAILED')
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED'

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $uninstallJournal -Operation 'agent-uninstall' `
        -Stage 'completed' -Status 'failed' -ErrorCodes @('REMOVE_PROGRAM_FAILED')
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED'

    Reset-TestRoot -Path $testRoot
    Write-SswOperationJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -TransactionId ([Guid]::NewGuid().ToString('N')) -Stage 'prepared' -Status 'running' `
        -Version 'test'
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED'
    Write-SswOperationJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -TransactionId ([Guid]::NewGuid().ToString('N')) -Stage 'completed' -Status 'succeeded' `
        -Version 'test'
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot

    $completedBytes = [IO.File]::ReadAllBytes($installJournal)
    $lockedHandle = [IO.File]::Open(
        $installJournal,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        $writerFailure = $null
        try {
            Write-SswOperationJournal -Path $installJournal `
                -Operation 'agent-install-or-update' `
                -TransactionId ([Guid]::NewGuid().ToString('N')) `
                -Stage 'prepared' -Status 'running' -Version 'test'
        }
        catch { $writerFailure = $_.Exception.Message }
        Assert-JournalTest -Condition (-not [string]::IsNullOrWhiteSpace($writerFailure)) `
            -Message 'A locked canonical journal must reject replacement.'
    }
    finally {
        $lockedHandle.Dispose()
    }
    Assert-JournalTest -Condition (
        [Convert]::ToBase64String($completedBytes) -ceq
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($installJournal))
    ) -Message 'Failed replacement must preserve the previous canonical journal bytes.'
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot

    Reset-TestRoot -Path $testRoot
    $orphanTemp = Join-Path $testRoot (
        'agent-install-or-update.json.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $orphanBackup = Join-Path $testRoot (
        'agent-uninstall.json.{0}.bak' -f [Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($orphanTemp, '{broken')
    [IO.File]::WriteAllText($orphanBackup, '{broken')
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot

    $invalidCases = @(
        [pscustomobject]@{ Name = 'format'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'completed' -Status 'succeeded' -FormatVersion 2
        } },
        [pscustomobject]@{ Name = 'operation'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-uninstall' `
                -Stage 'completed' -Status 'succeeded'
        } },
        [pscustomobject]@{ Name = 'transaction'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'completed' -Status 'succeeded' -TransactionId '../outside'
        } },
        [pscustomobject]@{ Name = 'stage'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'unknown' -Status 'running'
        } },
        [pscustomobject]@{ Name = 'time'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'completed' -Status 'succeeded' -UpdatedUtc 'not-a-time'
        } },
        [pscustomobject]@{ Name = 'error-code'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'rollback-completed' -Status 'failed' -ErrorCodes @('../bad')
        } },
        [pscustomobject]@{ Name = 'terminal-errors'; Writer = {
            Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
                -Stage 'completed' -Status 'succeeded' -ErrorCodes @('UNEXPECTED_ERROR')
        } }
    )
    foreach ($invalidCase in $invalidCases) {
        Reset-TestRoot -Path $testRoot
        & $invalidCase.Writer
        Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
            -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'
    }

    Reset-TestRoot -Path $testRoot
    [IO.File]::WriteAllText($installJournal, '{broken')
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    [IO.File]::WriteAllText($installJournal, '[]')
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    $missingPropertyPayload = [ordered]@{
        formatVersion = 1
        product = 'SamsungSwitchWatch'
        operation = 'agent-install-or-update'
        transactionId = [Guid]::NewGuid().ToString('N')
        stage = 'completed'
        status = 'succeeded'
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json
    [IO.File]::WriteAllText($installJournal, $missingPropertyPayload)
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    $scalarErrorPayload = [ordered]@{
        formatVersion = 1
        product = 'SamsungSwitchWatch'
        operation = 'agent-install-or-update'
        transactionId = [Guid]::NewGuid().ToString('N')
        stage = 'rollback-completed'
        status = 'failed'
        version = 'test'
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        errorCodes = 'RESTORE_PROGRAM_FAILED'
    } | ConvertTo-Json
    [IO.File]::WriteAllText($installJournal, $scalarErrorPayload)
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
        -Stage 'completed' -Status 'succeeded'
    $validBytes = [IO.File]::ReadAllBytes($installJournal)
    $paddingLength = 65536 - $validBytes.Length
    Assert-JournalTest -Condition ($paddingLength -gt 0) `
        -Message 'The valid journal fixture must fit below 64 KiB.'
    $boundaryPayload = [Text.Encoding]::UTF8.GetString($validBytes) + (' ' * $paddingLength)
    [IO.File]::WriteAllText(
        $installJournal,
        $boundaryPayload,
        (New-Object Text.UTF8Encoding($false)))
    Assert-JournalTest -Condition ((Get-Item -LiteralPath $installJournal).Length -eq 65536) `
        -Message 'Journal boundary fixture must be exactly 64 KiB.'
    Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot
    [IO.File]::AppendAllText(
        $installJournal,
        ' ',
        (New-Object Text.UTF8Encoding($false)))
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    New-Item -ItemType Directory -Path $installJournal | Out-Null
    Assert-GuardFailurePreservesTree -OperationsRoot $testRoot `
        -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID'

    Reset-TestRoot -Path $testRoot
    Remove-Item -LiteralPath $testRoot -Recurse -Force
    [IO.File]::WriteAllText($testRoot, 'not-a-directory')
    Assert-JournalFailure -Code 'AGENT_DEPLOYMENT_JOURNAL_INVALID' -Action {
        Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot
    }

    if (Test-SswAdministrator) {
        Write-SswStep 'Agent operations root migration and interrupted deployment integration'
        if (Test-Path -LiteralPath $fixtureRoot) {
            Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $fixtureRoot
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        Write-TestJournal -Path $installJournal -Operation 'agent-install-or-update' `
            -Stage 'prepared' -Status 'running'
        $legacyTransaction = Join-Path $testRoot 'transactions\33333333333333333333333333333333'
        New-Item -ItemType Directory -Path $legacyTransaction -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $legacyTransaction 'legacy.keep'), 'preserve')
        $legacyStaging = Join-Path $programRoot 'Agent.__staging_33333333333333333333333333333333'
        $legacyBackup = Join-Path $programRoot 'Agent.__backup_33333333333333333333333333333333'
        New-Item -ItemType Directory -Path $legacyStaging, $legacyBackup -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $legacyStaging 'new-agent.keep'), 'staging')
        [IO.File]::WriteAllText((Join-Path $legacyBackup 'old-agent.keep'), 'backup')

        $currentOwner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $legacyItems = @(
            Get-Item -LiteralPath $testRoot -Force
            Get-ChildItem -LiteralPath $testRoot -Recurse -Force
        )
        foreach ($legacyItem in $legacyItems) {
            $legacyAcl = Get-Acl -LiteralPath $legacyItem.FullName
            $legacyAcl.SetOwner($currentOwner)
            Set-Acl -LiteralPath $legacyItem.FullName -AclObject $legacyAcl
        }

        $integrationBefore = Get-TestTreeFingerprint -Path $fixtureRoot
        Initialize-SswAgentOperationsRoot -OperationsRoot $testRoot
        Assert-JournalFailure -Code 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED' -Action {
            Assert-SswAgentDeploymentJournalsReady -OperationsRoot $testRoot
        }
        Assert-JournalTest -Condition (
            (Get-TestTreeFingerprint -Path $fixtureRoot) -ceq $integrationBefore
        ) -Message 'Initialize then guard must preserve journal, transaction, staging, and backup content.'
        $securedItems = @(
            Get-Item -LiteralPath $testRoot -Force
            Get-ChildItem -LiteralPath $testRoot -Recurse -Force
        )
        foreach ($securedItem in $securedItems) {
            Assert-JournalTest -Condition (
                (ConvertTo-SswIdentitySid -Identity (Get-Acl -LiteralPath $securedItem.FullName).Owner) -eq
                'S-1-5-32-544'
            ) -Message "Legacy owner was not migrated: $($securedItem.FullName)"
        }
    }
    elseif ($RequireElevatedAclFixture) {
        throw 'Elevated Agent operations ACL migration fixture is required but this process is not elevated.'
    }
    else {
        Write-SswStep 'Skipped elevated Agent operations ACL migration fixture'
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $fixtureRoot
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Assert-JournalTest -Condition (-not (Test-Path -LiteralPath $fixtureRoot)) `
    -Message 'Agent deployment journal test files must be removed.'
Write-SswStep 'Agent deployment journal tests passed'
