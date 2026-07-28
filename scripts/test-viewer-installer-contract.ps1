Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-ViewerInstallerTest {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw $Message }
}

function Write-ViewerFixtureManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Manifest
    )

    [IO.File]::WriteAllText(
        $Path,
        ($Manifest | ConvertTo-Json -Depth 10),
        (New-Object Text.UTF8Encoding($false)))
}

function New-ViewerPackageFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Version = '0.0.0-test',
        [string]$Marker = 'deterministic-viewer-installer-contract-fixture'
    )

    New-Item -ItemType Directory -Path $Path | Out-Null
    $executablePath = Join-Path $Path 'SamsungSwitchWatch.Viewer.exe'
    [IO.File]::WriteAllText(
        $executablePath,
        "$Marker-$Version",
        (New-Object Text.UTF8Encoding($false)))
    $executable = Get-Item -LiteralPath $executablePath
    $hash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        manifestVersion = 1
        product = 'SamsungSwitchWatch'
        packageKind = 'Viewer'
        version = $Version
        sourceCommit = ('0' * 40)
        sourceDirty = $false
        repository = 'https://example.invalid/repository.git'
        runtimeIdentifier = 'win-x64'
        dotnetSdk = '10.0.0'
        builtUtc = '2026-01-01T00:00:00Z'
        signing = [ordered]@{
            status = 'unsigned-poc'
            certificateThumbprint = $null
            timestampUrl = $null
        }
        executable = [ordered]@{
            name = 'SamsungSwitchWatch.Viewer.exe'
            sha256 = $hash
            productVersion = $Version
        }
        files = @(
            [ordered]@{
                name = 'SamsungSwitchWatch.Viewer.exe'
                size = [long]$executable.Length
                sha256 = $hash
                authenticode = 'NotSigned'
            }
        )
    }
    Write-ViewerFixtureManifest -Path (Join-Path $Path 'BUILD-MANIFEST.json') `
        -Manifest $manifest
}

function Copy-ViewerPackageFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
}

function Read-ViewerFixtureManifest {
    param([Parameter(Mandatory = $true)][string]$Directory)

    return Get-Content -LiteralPath (Join-Path $Directory 'BUILD-MANIFEST.json') `
        -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Assert-ViewerPreflightRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Installer,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$MessagePattern,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $actualFailure = $null
    try {
        & $Installer -SourceDirectory $Source -InstallDirectory $InstallDirectory -PerUser `
            -Preflight | Out-Null
    }
    catch {
        $actualFailure = $_.Exception.Message
    }
    Assert-ViewerInstallerTest -Condition (
        -not [string]::IsNullOrWhiteSpace([string]$actualFailure) -and
        [string]$actualFailure -like $MessagePattern
    ) -Message "$FailureMessage Actual: $actualFailure"
}

$installer = Join-Path $PSScriptRoot 'install-viewer.ps1'
$installerText = Get-Content -LiteralPath $installer -Raw -Encoding UTF8
$uninstallerText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'uninstall-viewer.ps1') `
    -Raw -Encoding UTF8
$launcherText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-or-Update-Viewer.cmd') `
    -Raw -Encoding UTF8
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-viewer-installer-$testId"
$validFixture = Join-Path $testRoot 'valid'
$installDirectory = Join-Path $env:LOCALAPPDATA (
    "Programs\SamsungSwitchWatch\Viewer\contract-$testId")

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    New-ViewerPackageFixture -Path $validFixture

    & $installer -SourceDirectory $validFixture -InstallDirectory $installDirectory -PerUser `
        -Preflight | Out-Null

    $missingPayload = Join-Path $testRoot 'missing-payload'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $missingPayload
    Remove-Item -LiteralPath (Join-Path $missingPayload 'SamsungSwitchWatch.Viewer.exe') -Force
    Assert-ViewerPreflightRejected -Installer $installer -Source $missingPayload `
        -InstallDirectory $installDirectory -MessagePattern 'VIEWER_PACKAGE_FILE_MISSING:*' `
        -FailureMessage 'A missing declared payload file did not return its stable error code.'

    $extraPayload = Join-Path $testRoot 'extra-payload'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $extraPayload
    [IO.File]::WriteAllText(
        (Join-Path $extraPayload 'undeclared.bin'),
        'undeclared',
        (New-Object Text.UTF8Encoding($false)))
    Assert-ViewerPreflightRejected -Installer $installer -Source $extraPayload `
        -InstallDirectory $installDirectory -MessagePattern '*BUILD-MANIFEST.json*' `
        -FailureMessage 'An undeclared source payload file was accepted.'

    $nestedPayload = Join-Path $testRoot 'nested-payload'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $nestedPayload
    New-Item -ItemType Directory -Path (Join-Path $nestedPayload 'nested') | Out-Null
    Assert-ViewerPreflightRejected -Installer $installer -Source $nestedPayload `
        -InstallDirectory $installDirectory -MessagePattern '*nested*' `
        -FailureMessage 'A source payload directory was accepted.'

    $duplicateName = Join-Path $testRoot 'duplicate-name'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $duplicateName
    $duplicateManifest = Read-ViewerFixtureManifest -Directory $duplicateName
    $duplicateEntry = [pscustomobject]@{
        name = 'samsungswitchwatch.viewer.exe'
        size = $duplicateManifest.files[0].size
        sha256 = $duplicateManifest.files[0].sha256
        authenticode = $duplicateManifest.files[0].authenticode
    }
    $duplicateManifest.files = @($duplicateManifest.files[0], $duplicateEntry)
    Write-ViewerFixtureManifest -Path (Join-Path $duplicateName 'BUILD-MANIFEST.json') `
        -Manifest $duplicateManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $duplicateName `
        -InstallDirectory $installDirectory -MessagePattern '*SamsungSwitchWatch.Viewer.exe*' `
        -FailureMessage 'A duplicate case-insensitive manifest name was accepted.'

    $unsafeName = Join-Path $testRoot 'unsafe-name'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $unsafeName
    $unsafeManifest = Read-ViewerFixtureManifest -Directory $unsafeName
    $unsafeManifest.files[0].name = '..\SamsungSwitchWatch.Viewer.exe'
    Write-ViewerFixtureManifest -Path (Join-Path $unsafeName 'BUILD-MANIFEST.json') `
        -Manifest $unsafeManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $unsafeName `
        -InstallDirectory $installDirectory -MessagePattern '*SamsungSwitchWatch.Viewer.exe*' `
        -FailureMessage 'An unsafe manifest path was accepted.'

    $wrongSize = Join-Path $testRoot 'wrong-size'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $wrongSize
    $wrongSizeManifest = Read-ViewerFixtureManifest -Directory $wrongSize
    $wrongSizeManifest.files[0].size = [long]$wrongSizeManifest.files[0].size + 1
    Write-ViewerFixtureManifest -Path (Join-Path $wrongSize 'BUILD-MANIFEST.json') `
        -Manifest $wrongSizeManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $wrongSize `
        -InstallDirectory $installDirectory -MessagePattern 'VIEWER_PACKAGE_HASH_MISMATCH:*' `
        -FailureMessage 'A manifest size mismatch did not return its stable error code.'

    $wrongHash = Join-Path $testRoot 'wrong-hash'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $wrongHash
    $wrongHashManifest = Read-ViewerFixtureManifest -Directory $wrongHash
    $wrongHashManifest.files[0].sha256 = ('0' * 64)
    $wrongHashManifest.executable.sha256 = ('0' * 64)
    Write-ViewerFixtureManifest -Path (Join-Path $wrongHash 'BUILD-MANIFEST.json') `
        -Manifest $wrongHashManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $wrongHash `
        -InstallDirectory $installDirectory -MessagePattern 'VIEWER_PACKAGE_HASH_MISMATCH:*' `
        -FailureMessage 'A manifest hash mismatch did not return its stable error code.'

    $wrongExecutableIdentity = Join-Path $testRoot 'wrong-executable-identity'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $wrongExecutableIdentity
    $identityManifest = Read-ViewerFixtureManifest -Directory $wrongExecutableIdentity
    $identityManifest.executable.name = 'Other.Viewer.exe'
    Write-ViewerFixtureManifest `
        -Path (Join-Path $wrongExecutableIdentity 'BUILD-MANIFEST.json') `
        -Manifest $identityManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $wrongExecutableIdentity `
        -InstallDirectory $installDirectory -MessagePattern '*identity*' `
        -FailureMessage 'A wrong executable identity was accepted.'

    $wrongPackageIdentity = Join-Path $testRoot 'wrong-package-identity'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $wrongPackageIdentity
    $packageManifest = Read-ViewerFixtureManifest -Directory $wrongPackageIdentity
    $packageManifest.product = 'OtherProduct'
    Write-ViewerFixtureManifest -Path (Join-Path $wrongPackageIdentity 'BUILD-MANIFEST.json') `
        -Manifest $packageManifest
    Assert-ViewerPreflightRejected -Installer $installer -Source $wrongPackageIdentity `
        -InstallDirectory $installDirectory -MessagePattern '*Viewer*' `
        -FailureMessage 'A wrong package identity was accepted.'

    foreach ($needle in @(
        'Get-SswValidatedViewerPackage -Directory $source',
        'Get-SswValidatedViewerPackage -Directory $staging',
        'Get-SswValidatedViewerPackage -Directory $install',
        '[Environment]::Is64BitOperatingSystem',
        '$stagedPackage.ManifestSha256 -ne $sourcePackage.ManifestSha256',
        '$installedPackage.ManifestSha256 -ne $stagedPackage.ManifestSha256',
        'foreach ($file in @($sourcePackage.Files))',
        "-ArgumentList '--install-smoke-check'",
        '$process.WaitForExit(20000)',
        "'VIEWER_INSTALL_PATH_EXECUTION_BLOCKED'",
        "'VIEWER_SELF_CHECK_ACCESS_DENIED'",
        "'FILE_MISSING'",
        "'BAD_IMAGE'",
        "'TIMEOUT:",
        "'VIEWER_SOURCE_ACCESS_DENIED:",
        'VIEWER_SELF_CHECK_START_FAILED',
        'VIEWER_SELF_CHECK_WAIT_FAILED',
        'VIEWER_SELF_CHECK_EXITED_NONZERO',
        'Invoke-SswViewerElevatedMachinePhase -InstallerPath $PSCommandPath',
        'Invoke-SswViewerElevatedRollbackPhase -InstallerPath $PSCommandPath',
        '[switch]$MachineRollbackPhase',
        'function Invoke-SswViewerMachineRollbackCore',
        'function Get-SswValidatedViewerRollbackPackage',
        'function Write-SswViewerRollbackTransaction',
        'function Confirm-SswViewerRollbackTransaction',
        'function Remove-SswViewerRollbackTransaction',
        'VIEWER_ROLLBACK_TRANSACTION_MISSING',
        'VIEWER_ROLLBACK_TRANSACTION_TRUST_INVALID',
        'VIEWER_ROLLBACK_TRANSACTION_INVALID',
        '-InstallTransactionId $outerInstallTransactionId',
        'VIEWER_ROLLBACK_ACTIVE_CHANGED',
        '-ExpectedActiveManifestSha256 $sourcePackage.ManifestSha256',
        '"$resolvedInstall.__rollback"',
        'VIEWER_MACHINE_ROLLBACK_INCOMPLETE',
        'VIEWER_CURRENT_SELF_CHECK_FAILED',
        'Recovery: ROLLBACK_INCOMPLETE',
        'CURRENT_VIEWER_PRESERVED',
        'Invoke-SswViewerUserIntegration -ViewerExecutable $viewerExe',
        'Preserve-SswLegacyViewerInstall -LegacyDirectory $legacyInstall',
        'VIEWER_LEGACY_INSTALL_PRESERVED_RECOVERABLE',
        "Join-Path `$env:ProgramFiles 'SamsungSwitchWatch\Viewer'",
        "Join-Path `$env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Viewer'",
        'if ($MachinePhase) { Assert-SswAdministrator }',
        'Write-Host "Detail: $displayDetailCode"',
        'Write-Host "ExitCode: $failureExitCode"',
        '$diagnosticCodes += $failureDetailCode',
        'Install journal: $journalPath',
        'Viewer runtime diagnostic: $viewerRuntimeDiagnosticPath',
        "'VIEWER_POST_START_FAILED:"
    )) {
        Assert-ViewerInstallerTest -Condition $installerText.Contains($needle) `
            -Message "Viewer installer contract is missing: $needle"
    }
    Assert-ViewerInstallerTest -Condition (-not $installerText.Contains('Start-Sleep -Seconds 5')) `
        -Message 'The installer still uses the old five-second GUI smoke check.'
    foreach ($forbidden in @('Unblock-File', 'icacls.exe', '-ExecutionPolicy Bypass')) {
        Assert-ViewerInstallerTest -Condition (-not $installerText.Contains($forbidden)) `
            -Message "The installer contains a forbidden security bypass: $forbidden"
        Assert-ViewerInstallerTest -Condition (-not $launcherText.Contains($forbidden)) `
            -Message "The Viewer launcher contains a forbidden security bypass: $forbidden"
    }
    Assert-ViewerInstallerTest -Condition $uninstallerText.Contains(
        "Join-Path `$env:ProgramFiles 'SamsungSwitchWatch\Viewer'") `
        -Message 'The Viewer uninstaller does not default to the machine program path.'
    Assert-ViewerInstallerTest -Condition $uninstallerText.Contains(
        'if (-not $PerUser -and -not $MachinePhase)') `
        -Message 'The Viewer uninstaller is missing its original-user/elevated-machine split.'
    foreach ($needle in @(
        'function Test-SswOwnedViewerShortcut',
        '$shortcut.TargetPath',
        'SamsungSwitchWatch.Viewer.exe',
        'VIEWER_SHORTCUT_PRESERVED_UNVERIFIED',
        '$machineRollbackSlot = "$install.__rollback"',
        '$machineRollbackTransactionMarker = "$install.__rollback-transaction.json"',
        'Assert-SswTrustedDirectoryRootOwner -Path $machineRollbackSlot',
        'Assert-SswAdministratorsOnlyFileAcl -Path $machineRollbackTransactionMarker',
        "Name = 'remove-machine-rollback-slot'",
        "Name = 'remove-machine-rollback-transaction'",
        '$uninstallState.ActiveProgramRemoved',
        '$uninstallState.RollbackSlotRemoved',
        'VIEWER_UNINSTALL_ROLLBACK_PRESERVED',
        'VIEWER_UNINSTALL_TRANSACTION_PRESERVED',
        'VIEWER_UNINSTALL_SHORTCUTS_PRESERVED',
        'VIEWER_UNINSTALL_SETTINGS_PRESERVED'
    )) {
        Assert-ViewerInstallerTest -Condition $uninstallerText.Contains($needle) `
            -Message "Viewer uninstaller ownership contract is missing: $needle"
    }
    $uninstallProgramIndex = $uninstallerText.IndexOf("Name = 'remove-program'")
    $uninstallSlotIndex = $uninstallerText.IndexOf(
        "Name = 'remove-machine-rollback-slot'", $uninstallProgramIndex)
    $uninstallMarkerIndex = $uninstallerText.IndexOf(
        "Name = 'remove-machine-rollback-transaction'", $uninstallSlotIndex)
    Assert-ViewerInstallerTest -Condition (
        $uninstallProgramIndex -ge 0 -and
        $uninstallSlotIndex -gt $uninstallProgramIndex -and
        $uninstallMarkerIndex -gt $uninstallSlotIndex
    ) -Message 'Viewer uninstall removes the rollback transaction before active/slot cleanup.'

    $swapIndex = $installerText.IndexOf('$installSwapped = $true')
    $installedValidationIndex = $installerText.IndexOf(
        'Get-SswValidatedViewerPackage -Directory $install', $swapIndex)
    $smokeIndex = $installerText.IndexOf(
        'Invoke-SswViewerSelfCheck -ViewerExecutable $viewerExe', $installedValidationIndex)
    $rollbackTransactionWriteIndex = $installerText.IndexOf(
        'Write-SswViewerRollbackTransaction -InstallDirectory $install', $smokeIndex)
    $commitIndex = $installerText.IndexOf("-Stage 'completed' -Status 'succeeded'", $smokeIndex)
    $committedFlagIndex = $installerText.IndexOf('$transactionCommitted = $true', $commitIndex)
    $normalStartIndex = $installerText.IndexOf(
        'if (-not $MachinePhase -and -not $DoNotStart)', $committedFlagIndex)
    Assert-ViewerInstallerTest -Condition (
        $swapIndex -ge 0 -and
        $installedValidationIndex -gt $swapIndex -and
        $smokeIndex -gt $installedValidationIndex -and
        $rollbackTransactionWriteIndex -gt $smokeIndex -and
        $commitIndex -gt $rollbackTransactionWriteIndex -and
        $committedFlagIndex -gt $commitIndex -and
        $normalStartIndex -gt $committedFlagIndex
    ) -Message 'Installed revalidation, smoke, durable commit, and normal start are out of order.'

    $outerFlowIndex = $installerText.IndexOf(
        'if (-not $PerUser -and -not $MachinePhase) {')
    $elevationIndex = $installerText.IndexOf(
        'Invoke-SswViewerElevatedMachinePhase -InstallerPath $PSCommandPath', $outerFlowIndex)
    $userIntegrationIndex = $installerText.IndexOf(
        'Invoke-SswViewerUserIntegration -ViewerExecutable $viewerExe', $elevationIndex)
    $legacyCleanupIndex = $installerText.IndexOf(
        'Preserve-SswLegacyViewerInstall -LegacyDirectory $legacyInstall', $userIntegrationIndex)
    Assert-ViewerInstallerTest -Condition (
        $outerFlowIndex -ge 0 -and
        $elevationIndex -gt $outerFlowIndex -and
        $userIntegrationIndex -gt $elevationIndex -and
        $legacyCleanupIndex -gt $userIntegrationIndex
    ) -Message 'Current-user integration or verified legacy cleanup occurs before machine success.'

    $outerRollbackIndex = $installerText.IndexOf(
        'Invoke-SswViewerElevatedRollbackPhase -InstallerPath $PSCommandPath',
        $userIntegrationIndex)
    Assert-ViewerInstallerTest -Condition (
        $outerRollbackIndex -gt $userIntegrationIndex -and
        $outerRollbackIndex -lt $legacyCleanupIndex
    ) -Message 'Original-user failure must request elevated rollback before legacy handling.'

    $initializeStart = $installerText.IndexOf(
        'function Initialize-SswViewerMachineRollbackSlot')
    $rotationHelperStart = $installerText.IndexOf(
        'function Move-SswViewerCurrentInstallToRollbackSlot', $initializeStart)
    $coreStart = $installerText.IndexOf(
        'function Invoke-SswViewerMachineRollbackCore', $rotationHelperStart)
    $initializeText = $installerText.Substring(
        $initializeStart,
        $rotationHelperStart - $initializeStart)
    Assert-ViewerInstallerTest -Condition $initializeText.Contains(
        'Invoke-SswViewerMachineRollbackCore -InstallDirectory $InstallDirectory') `
        -Message 'A damaged current install must retain core rollback recovery.'
    Assert-ViewerInstallerTest -Condition $initializeText.Contains(
        'Get-SswValidatedViewerPackage -Directory $InstallDirectory') `
        -Message 'Pre-update initialization must validate the active Viewer before slot rotation.'
    Assert-ViewerInstallerTest -Condition $initializeText.Contains(
        'Invoke-SswViewerSelfCheck') `
        -Message 'Pre-update initialization must prove the active Viewer is executable.'
    Assert-ViewerInstallerTest -Condition (-not $initializeText.Contains(
        'Remove-Item -LiteralPath $rollbackSlot')) `
        -Message 'Pre-update initialization retires rollback evidence before staging is ready.'
    $rollbackPhaseStart = $installerText.IndexOf(
        'function Invoke-SswViewerMachineRollbackPhase')
    $rollbackPhaseEnd = $installerText.IndexOf(
        'function Invoke-SswViewerUserIntegration', $rollbackPhaseStart)
    $rollbackPhaseText = $installerText.Substring(
        $rollbackPhaseStart,
        $rollbackPhaseEnd - $rollbackPhaseStart)
    $rollbackConfirmIndex = $rollbackPhaseText.IndexOf(
        'Confirm-SswViewerRollbackTransaction')
    $rollbackCoreIndex = $rollbackPhaseText.IndexOf(
        'Invoke-SswViewerMachineRollbackCore', $rollbackConfirmIndex)
    $rollbackMarkerRemoveIndex = $rollbackPhaseText.IndexOf(
        'Remove-SswViewerRollbackTransaction', $rollbackCoreIndex)
    Assert-ViewerInstallerTest -Condition (
        $rollbackConfirmIndex -ge 0 -and
        $rollbackCoreIndex -gt $rollbackConfirmIndex -and
        $rollbackMarkerRemoveIndex -gt $rollbackCoreIndex
    ) -Message 'Rollback transaction marker is removed before file recovery succeeds.'

    $cleanupStart = $installerText.IndexOf("Name = 'cleanup-program-backup'")
    $cleanupEnd = $installerText.IndexOf("Name = 'cleanup-shortcut-backup'", $cleanupStart)
    $cleanupText = $installerText.Substring($cleanupStart, $cleanupEnd - $cleanupStart)
    Assert-ViewerInstallerTest -Condition $cleanupText.Contains('-not $MachinePhase') `
        -Message 'Machine commit must retain its fixed rollback slot.'

    $perUserMutationIndex = $installerText.IndexOf(
        '$shortcutMutationStarted = $true', $smokeIndex)
    $perUserIntegrationIndex = $installerText.IndexOf(
        'Invoke-SswViewerUserIntegration -ViewerExecutable $viewerExe',
        $perUserMutationIndex)
    Assert-ViewerInstallerTest -Condition (
        $perUserMutationIndex -gt $smokeIndex -and
        $perUserIntegrationIndex -gt $perUserMutationIndex
    ) -Message 'PerUser shortcut rollback state must be set immediately before integration.'

    $legacyFunctionStart = $installerText.IndexOf(
        'function Preserve-SswLegacyViewerInstall')
    $legacyFunctionEnd = $installerText.IndexOf(
        "Write-SswStep 'Viewer", $legacyFunctionStart)
    $legacyFunctionText = $installerText.Substring(
        $legacyFunctionStart,
        $legacyFunctionEnd - $legacyFunctionStart)
    Assert-ViewerInstallerTest -Condition (-not $legacyFunctionText.Contains(
        'Remove-Item -LiteralPath $LegacyDirectory')) `
        -Message 'Legacy per-user program files must remain recoverable.'

    . $installer -SourceDirectory $validFixture -InstallDirectory $installDirectory `
        -PerUser -Preflight | Out-Null
    $fixtureMachineInstall = Join-Path $testRoot 'machine-layout\Viewer'
    $fixtureRollbackSlot = Get-SswViewerMachineRollbackSlot `
        -InstallDirectory $fixtureMachineInstall
    Assert-ViewerInstallerTest -Condition (
        $fixtureRollbackSlot -ceq "$fixtureMachineInstall.__rollback" -and
        (Split-Path $fixtureRollbackSlot -Parent) -ceq
            (Split-Path $fixtureMachineInstall -Parent)
    ) -Message 'The fixed rollback slot is not a strict install sibling.'
    foreach ($invalidInternalInvocation in @(
        [pscustomobject]@{
            Arguments = @(
                '-MachinePhase',
                '-SourceDirectory', $validFixture,
                '-InstallDirectory', $fixtureMachineInstall)
            Name = 'MachinePhase without transaction ID'
        },
        [pscustomobject]@{
            Arguments = @(
                '-MachineRollbackPhase',
                '-InstallDirectory', $fixtureMachineInstall,
                '-InstallTransactionId', ('a' * 32))
            Name = 'MachineRollbackPhase without expected active hash'
        },
        [pscustomobject]@{
            Arguments = @(
                '-MachineRollbackPhase',
                '-InstallDirectory', $fixtureMachineInstall,
                '-ExpectedActiveManifestSha256', ('b' * 64))
            Name = 'MachineRollbackPhase without transaction ID'
        },
        [pscustomobject]@{
            Arguments = @(
                '-MachinePhase',
                '-SourceDirectory', $validFixture,
                '-InstallDirectory', $fixtureMachineInstall,
                '-InstallTransactionId', ('c' * 32),
                '-DoNotStart')
            Name = 'MachinePhase with user-only DoNotStart'
        }
    )) {
        $modeFailure = $null
        try {
            $invocationArguments = [object[]]$invalidInternalInvocation.Arguments
            & $installer @invocationArguments | Out-Null
        }
        catch {
            $modeFailure = $_.Exception.Message
        }
        Assert-ViewerInstallerTest -Condition (
            [string]$modeFailure -like 'VIEWER_INSTALL_MODE_INVALID:*'
        ) -Message "$($invalidInternalInvocation.Name) bypassed fail-closed mode validation. Actual: $modeFailure"
    }

    $fixtureCurrent = Join-Path $testRoot 'rollback-current'
    $fixtureSlot = Join-Path $testRoot 'rollback-slot'
    Copy-ViewerPackageFixture -Source $validFixture -Destination $fixtureCurrent
    Copy-ViewerPackageFixture -Source $validFixture -Destination $fixtureSlot
    $fixtureCurrentPackage = Get-SswValidatedViewerPackage -Directory $fixtureCurrent `
        -ManifestPath (Join-Path $fixtureCurrent 'BUILD-MANIFEST.json')
    $fixtureSlotPackage = Get-SswValidatedViewerPackage -Directory $fixtureSlot `
        -ManifestPath (Join-Path $fixtureSlot 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $fixtureCurrentPackage.ManifestSha256 -eq $fixtureSlotPackage.ManifestSha256
    ) -Message 'Current/rollback fixture validation is not deterministic.'

    function Get-Process {
        [CmdletBinding()]
        param([string]$Name)
        if ($Name -eq 'SamsungSwitchWatch.Viewer') { return }
        Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
    }
    function Stop-Process {
        [CmdletBinding()]
        param([Parameter(ValueFromPipeline = $true)]$InputObject)
        throw 'VIEWER_INSTALLER_TEST_ISOLATION_FAILED: fixture rollback must not stop a real process.'
    }
    $script:viewerFixtureSelfCheckFailurePath = $null
    function Invoke-SswViewerSelfCheck {
        param(
            [Parameter(Mandatory = $true)][string]$ViewerExecutable,
            [Parameter(Mandatory = $true)][string]$WorkingDirectory
        )
        if ($WorkingDirectory -ceq $script:viewerFixtureSelfCheckFailurePath) {
            throw 'TIMEOUT: synthetic current Viewer self-check timeout'
        }
    }
    function Assert-SswTrustedDirectoryRootOwner {
        param([Parameter(Mandatory = $true)][string]$Path)
        return 'S-1-5-32-544'
    }
    function Set-SswAdministratorsOnlyFileAcl {
        param([Parameter(Mandatory = $true)][string]$Path)
    }
    $script:viewerFixtureUntrustedMarkerPath = $null
    function Assert-SswAdministratorsOnlyFileAcl {
        param([Parameter(Mandatory = $true)][string]$Path)
        if (-not [string]::IsNullOrWhiteSpace(
                [string]$script:viewerFixtureUntrustedMarkerPath) -and
            [IO.Path]::GetFullPath($Path) -ceq
                [IO.Path]::GetFullPath($script:viewerFixtureUntrustedMarkerPath)) {
            throw 'synthetic untrusted rollback marker ACL'
        }
    }

    # V1 -> V2 성공 뒤에는 active=V2, slot=V1입니다. V3 업데이트 준비가
    # 정상 V2를 V1로 되돌리면 V3 실패 시 직전 버전이 아닌 V1이 복원됩니다.
    $generationRoot = Join-Path $testRoot 'three-generation'
    $version1Fixture = Join-Path $generationRoot 'package-v1'
    $version2Fixture = Join-Path $generationRoot 'package-v2'
    $version3Fixture = Join-Path $generationRoot 'package-v3'
    New-ViewerPackageFixture -Path $version1Fixture -Version '1.0.0-test' -Marker 'viewer-v1'
    New-ViewerPackageFixture -Path $version2Fixture -Version '2.0.0-test' -Marker 'viewer-v2'
    New-ViewerPackageFixture -Path $version3Fixture -Version '3.0.0-test' -Marker 'viewer-v3'
    $generationInstall = Join-Path $generationRoot 'machine\Viewer'
    $generationSlot = "$generationInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $generationInstall
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $generationSlot

    $preparedSlot = Initialize-SswViewerMachineRollbackSlot `
        -InstallDirectory $generationInstall
    $preparedPackage = Get-SswValidatedViewerPackage -Directory $generationInstall `
        -ManifestPath (Join-Path $generationInstall 'BUILD-MANIFEST.json')
    $preparedRollback = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $generationSlot
    $version1Package = Get-SswValidatedViewerPackage -Directory $version1Fixture `
        -ManifestPath (Join-Path $version1Fixture 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $preparedSlot -ceq $generationSlot -and
        [string]$preparedPackage.Manifest.version -ceq '2.0.0-test' -and
        $preparedRollback.ManifestSha256 -eq $version1Package.ManifestSha256
    ) -Message 'V3 preparation did not preserve active V2 and stale V1 until swap.'

    $generationMoved = $false
    $rotatedPackage = Move-SswViewerCurrentInstallToRollbackSlot `
        -InstallDirectory $generationInstall `
        -MovedToRollbackSlot ([ref]$generationMoved)
    Assert-ViewerInstallerTest -Condition (
        $generationMoved -and
        [string]$rotatedPackage.Manifest.version -ceq '2.0.0-test' -and
        -not (Test-Path -LiteralPath $generationInstall)
    ) -Message 'The production slot-rotation helper did not preserve V2 as the rollback package.'
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $generationInstall
    $version3Package = Get-SswValidatedViewerPackage -Directory $generationInstall `
        -ManifestPath (Join-Path $generationInstall 'BUILD-MANIFEST.json')
    $version2Package = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $generationSlot
    $version3TransactionId = ('3' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $generationInstall `
        -TransactionId $version3TransactionId `
        -ActiveManifestSha256 $version3Package.ManifestSha256 `
        -RollbackManifestSha256 $version2Package.ManifestSha256
    Confirm-SswViewerRollbackTransaction -InstallDirectory $generationInstall `
        -TransactionId $version3TransactionId `
        -ExpectedActiveManifestSha256 $version3Package.ManifestSha256
    $threeGenerationRollback = Invoke-SswViewerMachineRollbackCore `
        -InstallDirectory $generationInstall
    Remove-SswViewerRollbackTransaction -InstallDirectory $generationInstall `
        -TransactionId $version3TransactionId `
        -ExpectedActiveManifestSha256 $version3Package.ManifestSha256
    $threeGenerationRestored = Get-SswValidatedViewerPackage -Directory $generationInstall `
        -ManifestPath (Join-Path $generationInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $threeGenerationRollback -ceq 'PREVIOUS_VIEWER_RESTORED' -and
        [string]$threeGenerationRestored.Manifest.version -ceq '2.0.0-test' -and
        [string]$threeGenerationRestored.Manifest.version -cne '1.0.0-test' -and
        -not (Test-Path -LiteralPath $generationSlot)
    ) -Message 'A failed V3 update did not restore the immediate previous V2 package.'

    # 같은 V3 ZIP을 A/B 두 설치가 연속으로 교체하면 manifest SHA-256은
    # 동일합니다. 고유 transaction ID가 없으면 A의 늦은 rollback이 B의
    # slot을 소비하고 B rollback이 활성 Viewer까지 제거할 수 있습니다.
    $samePackageRoot = Join-Path $testRoot 'same-package-race'
    $samePackageInstall = Join-Path $samePackageRoot 'machine\Viewer'
    $samePackageSlot = "$samePackageInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $samePackageInstall
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $samePackageSlot
    $samePackageActive = Get-SswValidatedViewerPackage -Directory $samePackageInstall `
        -ManifestPath (Join-Path $samePackageInstall 'BUILD-MANIFEST.json')
    $samePackageRollback = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $samePackageSlot
    $transactionA = ('a' * 32)
    $transactionB = ('b' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
        -TransactionId $transactionA `
        -ActiveManifestSha256 $samePackageActive.ManifestSha256 `
        -RollbackManifestSha256 $samePackageRollback.ManifestSha256
    $samePackageMoved = $false
    Move-SswViewerCurrentInstallToRollbackSlot `
        -InstallDirectory $samePackageInstall `
        -MovedToRollbackSlot ([ref]$samePackageMoved) | Out-Null
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $samePackageInstall
    $samePackageActive = Get-SswValidatedViewerPackage -Directory $samePackageInstall `
        -ManifestPath (Join-Path $samePackageInstall 'BUILD-MANIFEST.json')
    $samePackageRollback = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $samePackageSlot
    Assert-ViewerInstallerTest -Condition (
        $samePackageMoved -and
        $samePackageActive.ManifestSha256 -eq $samePackageRollback.ManifestSha256
    ) -Message 'The same-package A/B fixture did not produce identical active and rollback manifests.'
    Write-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
        -TransactionId $transactionB `
        -ActiveManifestSha256 $samePackageActive.ManifestSha256 `
        -RollbackManifestSha256 $samePackageRollback.ManifestSha256
    $samePackageOwningMarker = Read-SswViewerRollbackTransaction `
        -InstallDirectory $samePackageInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$samePackageOwningMarker.transactionId -ceq $transactionB -and
        [string]$samePackageOwningMarker.activeManifestSha256 -ceq
            $samePackageActive.ManifestSha256
    ) -Message 'The B transaction did not atomically supersede the existing A marker.'
    $staleTransactionFailure = $null
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
            -TransactionId $transactionA `
            -ExpectedActiveManifestSha256 $samePackageActive.ManifestSha256
    }
    catch {
        $staleTransactionFailure = $_.Exception.Message
    }
    $samePackageMarker = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $samePackageInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$staleTransactionFailure -like 'VIEWER_ROLLBACK_ACTIVE_CHANGED:*' -and
        (Test-Path -LiteralPath $samePackageInstall -PathType Container) -and
        (Test-Path -LiteralPath $samePackageSlot -PathType Container) -and
        (Test-Path -LiteralPath $samePackageMarker -PathType Leaf)
    ) -Message 'A stale same-package transaction mutated the active Viewer, slot, or marker.'

    Confirm-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
        -TransactionId $transactionB `
        -ExpectedActiveManifestSha256 $samePackageActive.ManifestSha256
    Assert-ViewerInstallerTest -Condition (
        Test-Path -LiteralPath $samePackageMarker -PathType Leaf
    ) -Message 'Rollback confirmation removed its marker before file recovery completed.'
    $samePackageRecovery = Invoke-SswViewerMachineRollbackCore `
        -InstallDirectory $samePackageInstall
    Remove-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
        -TransactionId $transactionB `
        -ExpectedActiveManifestSha256 $samePackageActive.ManifestSha256
    $samePackageRestored = Get-SswValidatedViewerPackage `
        -Directory $samePackageInstall `
        -ManifestPath (Join-Path $samePackageInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $samePackageRecovery -ceq 'PREVIOUS_VIEWER_RESTORED' -and
        $samePackageRestored.ManifestSha256 -eq $samePackageActive.ManifestSha256 -and
        -not (Test-Path -LiteralPath $samePackageSlot) -and
        -not (Test-Path -LiteralPath $samePackageMarker)
    ) -Message 'The owning same-package transaction did not consume its marker and restore its slot.'

    $consumedMarkerFailure = $null
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $samePackageInstall `
            -TransactionId $transactionB `
            -ExpectedActiveManifestSha256 $samePackageActive.ManifestSha256
    }
    catch {
        $consumedMarkerFailure = $_.Exception.Message
    }
    Assert-ViewerInstallerTest -Condition (
        [string]$consumedMarkerFailure -like 'VIEWER_ROLLBACK_TRANSACTION_MISSING:*' -and
        (Test-Path -LiteralPath $samePackageInstall -PathType Container)
    ) -Message 'A consumed rollback transaction could be replayed against the active Viewer.'

    $untrustedMarkerInstall = Join-Path $testRoot 'untrusted-marker\Viewer'
    $untrustedMarkerSlot = "$untrustedMarkerInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $untrustedMarkerInstall
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $untrustedMarkerSlot
    $untrustedMarkerActive = Get-SswValidatedViewerPackage `
        -Directory $untrustedMarkerInstall `
        -ManifestPath (Join-Path $untrustedMarkerInstall 'BUILD-MANIFEST.json')
    $untrustedMarkerRollback = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $untrustedMarkerSlot
    $untrustedTransactionId = ('e' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $untrustedMarkerInstall `
        -TransactionId $untrustedTransactionId `
        -ActiveManifestSha256 $untrustedMarkerActive.ManifestSha256 `
        -RollbackManifestSha256 $untrustedMarkerRollback.ManifestSha256
    $untrustedMarkerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $untrustedMarkerInstall
    $script:viewerFixtureUntrustedMarkerPath = $untrustedMarkerPath
    $untrustedMarkerFailure = $null
    $untrustedMarkerWriteFailure = $null
    try {
        Write-SswViewerRollbackTransaction -InstallDirectory $untrustedMarkerInstall `
            -TransactionId ('9' * 32) `
            -ActiveManifestSha256 $untrustedMarkerActive.ManifestSha256 `
            -RollbackManifestSha256 $untrustedMarkerRollback.ManifestSha256
    }
    catch {
        $untrustedMarkerWriteFailure = $_.Exception.Message
    }
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $untrustedMarkerInstall `
            -TransactionId $untrustedTransactionId `
            -ExpectedActiveManifestSha256 $untrustedMarkerActive.ManifestSha256
    }
    catch {
        $untrustedMarkerFailure = $_.Exception.Message
    }
    finally {
        $script:viewerFixtureUntrustedMarkerPath = $null
    }
    $preservedUntrustedMarker = Read-SswViewerRollbackTransaction `
        -InstallDirectory $untrustedMarkerInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$untrustedMarkerWriteFailure -like
            'VIEWER_ROLLBACK_TRANSACTION_TRUST_INVALID:*' -and
        [string]$untrustedMarkerFailure -like
            'VIEWER_ROLLBACK_TRANSACTION_TRUST_INVALID:*' -and
        [string]$preservedUntrustedMarker.transactionId -ceq
            $untrustedTransactionId -and
        (Test-Path -LiteralPath $untrustedMarkerInstall -PathType Container) -and
        (Test-Path -LiteralPath $untrustedMarkerSlot -PathType Container) -and
        (Test-Path -LiteralPath $untrustedMarkerPath -PathType Leaf)
    ) -Message 'An untrusted rollback marker did not fail closed with all evidence preserved.'

    $changedActiveInstall = Join-Path $testRoot 'changed-active\Viewer'
    $changedActiveSlot = "$changedActiveInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $changedActiveInstall
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $changedActiveSlot
    $expectedChangedActive = Get-SswValidatedViewerPackage `
        -Directory $changedActiveInstall `
        -ManifestPath (Join-Path $changedActiveInstall 'BUILD-MANIFEST.json')
    $changedActiveRollback = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $changedActiveSlot
    $changedActiveTransactionId = ('7' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $changedActiveInstall `
        -TransactionId $changedActiveTransactionId `
        -ActiveManifestSha256 $expectedChangedActive.ManifestSha256 `
        -RollbackManifestSha256 $changedActiveRollback.ManifestSha256
    Remove-Item -LiteralPath $changedActiveInstall -Recurse -Force
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $changedActiveInstall
    $changedActiveFailure = $null
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $changedActiveInstall `
            -TransactionId $changedActiveTransactionId `
            -ExpectedActiveManifestSha256 $expectedChangedActive.ManifestSha256
    }
    catch {
        $changedActiveFailure = $_.Exception.Message
    }
    $changedActiveActual = Get-SswValidatedViewerPackage `
        -Directory $changedActiveInstall `
        -ManifestPath (Join-Path $changedActiveInstall 'BUILD-MANIFEST.json')
    $changedActiveMarkerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $changedActiveInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$changedActiveFailure -like 'VIEWER_ROLLBACK_ACTIVE_CHANGED:*' -and
        [string]$changedActiveActual.Manifest.version -ceq '3.0.0-test' -and
        (Test-Path -LiteralPath $changedActiveSlot -PathType Container) -and
        (Test-Path -LiteralPath $changedActiveMarkerPath -PathType Leaf)
    ) -Message 'A different valid active package was changed by a stale rollback transaction.'

    $missingBoundSlotInstall = Join-Path $testRoot 'missing-bound-slot\Viewer'
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $missingBoundSlotInstall
    $missingBoundSlotActive = Get-SswValidatedViewerPackage `
        -Directory $missingBoundSlotInstall `
        -ManifestPath (Join-Path $missingBoundSlotInstall 'BUILD-MANIFEST.json')
    $missingBoundSlotTransactionId = ('6' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $missingBoundSlotInstall `
        -TransactionId $missingBoundSlotTransactionId `
        -ActiveManifestSha256 $missingBoundSlotActive.ManifestSha256 `
        -RollbackManifestSha256 $version2Package.ManifestSha256
    $missingBoundSlotFailure = $null
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $missingBoundSlotInstall `
            -TransactionId $missingBoundSlotTransactionId `
            -ExpectedActiveManifestSha256 $missingBoundSlotActive.ManifestSha256
    }
    catch {
        $missingBoundSlotFailure = $_.Exception.Message
    }
    $missingBoundSlotMarker = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $missingBoundSlotInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$missingBoundSlotFailure -like
            'VIEWER_ROLLBACK_TRANSACTION_INVALID:*' -and
        (Test-Path -LiteralPath $missingBoundSlotInstall -PathType Container) -and
        (Test-Path -LiteralPath $missingBoundSlotMarker -PathType Leaf)
    ) -Message 'A marker-bound missing rollback slot did not fail closed.'

    $wrongBoundSlotInstall = Join-Path $testRoot 'wrong-bound-slot\Viewer'
    $wrongBoundSlot = "$wrongBoundSlotInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version3Fixture -Destination $wrongBoundSlotInstall
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $wrongBoundSlot
    $wrongBoundSlotActive = Get-SswValidatedViewerPackage `
        -Directory $wrongBoundSlotInstall `
        -ManifestPath (Join-Path $wrongBoundSlotInstall 'BUILD-MANIFEST.json')
    $wrongBoundSlotTransactionId = ('5' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $wrongBoundSlotInstall `
        -TransactionId $wrongBoundSlotTransactionId `
        -ActiveManifestSha256 $wrongBoundSlotActive.ManifestSha256 `
        -RollbackManifestSha256 $version2Package.ManifestSha256
    $wrongBoundSlotFailure = $null
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $wrongBoundSlotInstall `
            -TransactionId $wrongBoundSlotTransactionId `
            -ExpectedActiveManifestSha256 $wrongBoundSlotActive.ManifestSha256
    }
    catch {
        $wrongBoundSlotFailure = $_.Exception.Message
    }
    $wrongBoundSlotMarker = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $wrongBoundSlotInstall
    Assert-ViewerInstallerTest -Condition (
        [string]$wrongBoundSlotFailure -like
            'VIEWER_ROLLBACK_TRANSACTION_INVALID:*' -and
        (Test-Path -LiteralPath $wrongBoundSlotInstall -PathType Container) -and
        (Test-Path -LiteralPath $wrongBoundSlot -PathType Container) -and
        (Test-Path -LiteralPath $wrongBoundSlotMarker -PathType Leaf)
    ) -Message 'A marker-bound rollback slot hash mismatch did not preserve all evidence.'

    $rotationHelperStart = $installerText.IndexOf(
        'function Move-SswViewerCurrentInstallToRollbackSlot')
    $rollbackCoreStart = $installerText.IndexOf(
        'function Invoke-SswViewerMachineRollbackCore', $rotationHelperStart)
    $rotationHelperText = $installerText.Substring(
        $rotationHelperStart,
        $rollbackCoreStart - $rotationHelperStart)
    $rotationValidationIndex = $rotationHelperText.IndexOf(
        'Get-SswValidatedViewerRollbackPackage -RollbackSlot $rollbackSlot')
    $rotationRemovalIndex = $rotationHelperText.IndexOf(
        'Remove-Item -LiteralPath $rollbackSlot', $rotationValidationIndex)
    $previousMoveIndex = $rotationHelperText.IndexOf(
        'Move-Item -LiteralPath $InstallDirectory -Destination $rollbackSlot',
        $rotationRemovalIndex)
    $backupReadyIndex = $rotationHelperText.IndexOf(
        '$MovedToRollbackSlot.Value = $true', $previousMoveIndex)
    $helperCallIndex = $installerText.IndexOf(
        'Move-SswViewerCurrentInstallToRollbackSlot -InstallDirectory $install')
    $newInstallMoveIndex = $installerText.IndexOf(
        'Move-Item -LiteralPath $staging -Destination $install', $helperCallIndex)
    Assert-ViewerInstallerTest -Condition (
        $rotationHelperStart -ge 0 -and
        $rotationValidationIndex -ge 0 -and
        $rotationRemovalIndex -gt $rotationValidationIndex -and
        $previousMoveIndex -gt $rotationRemovalIndex -and
        $backupReadyIndex -gt $previousMoveIndex -and
        $helperCallIndex -gt $rollbackCoreStart -and
        $newInstallMoveIndex -gt $helperCallIndex
    ) -Message 'Rollback slot refresh, previous-version backup, and new install swap are out of order.'
    $restoreProgramStart = $installerText.IndexOf("Name = 'restore-program'")
    $restoreProgramEnd = $installerText.IndexOf("Name = 'restore-shortcuts'", $restoreProgramStart)
    $restoreProgramText = $installerText.Substring(
        $restoreProgramStart,
        $restoreProgramEnd - $restoreProgramStart)
    Assert-ViewerInstallerTest -Condition $restoreProgramText.Contains(
        '$previousInstallMovedToBackup') `
        -Message 'Failure recovery can mistake a stale slot for this transaction backup.'
    $recoveryDecisionIndex = $installerText.IndexOf(
        'if ($previousInstallMovedToBackup)')
    $preservedDecisionIndex = $installerText.IndexOf(
        "elseif (`$previousInstallExisted -and (Test-Path -LiteralPath `$install -PathType Container))",
        $recoveryDecisionIndex)
    Assert-ViewerInstallerTest -Condition (
        $recoveryDecisionIndex -ge 0 -and
        $preservedDecisionIndex -gt $recoveryDecisionIndex -and
        $installerText.Contains(
            "Write-Warning '새 Viewer로 교체하기 전에 실패하여 현재 설치는 그대로 유지했습니다.'")
    ) -Message 'An untouched current install is not reported as preserved after a pre-swap failure.'

    # EDR 격리나 손상으로 현재 실행 파일/manifest가 사라져도 검증된 slot은
    # 현재 패키지 검증에 막히지 않고 복원되어야 한다.
    $selfCheckInstall = Join-Path $testRoot 'self-check-failure\Viewer'
    $selfCheckSlot = "$selfCheckInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $selfCheckInstall
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $selfCheckSlot
    $script:viewerFixtureSelfCheckFailurePath = $selfCheckInstall
    $selfCheckFailure = $null
    try {
        Initialize-SswViewerMachineRollbackSlot -InstallDirectory $selfCheckInstall |
            Out-Null
    }
    catch {
        $selfCheckFailure = $_.Exception.Message
    }
    finally {
        $script:viewerFixtureSelfCheckFailurePath = $null
    }
    $selfCheckActivePackage = Get-SswValidatedViewerPackage -Directory $selfCheckInstall `
        -ManifestPath (Join-Path $selfCheckInstall 'BUILD-MANIFEST.json')
    $selfCheckRollbackPackage = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $selfCheckSlot
    Assert-ViewerInstallerTest -Condition (
        [string]$selfCheckFailure -like 'VIEWER_CURRENT_SELF_CHECK_FAILED:*' -and
        [string]$selfCheckActivePackage.Manifest.version -ceq '2.0.0-test' -and
        [string]$selfCheckRollbackPackage.Manifest.version -ceq '1.0.0-test'
    ) -Message 'A transient current Viewer self-check failure did not preserve active V2 and slot V1.'

    $invalidSlotInstall = Join-Path $testRoot 'invalid-slot\Viewer'
    $invalidSlot = "$invalidSlotInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version2Fixture -Destination $invalidSlotInstall
    New-Item -ItemType Directory -Path $invalidSlot | Out-Null
    Set-Content -LiteralPath (Join-Path $invalidSlot 'damaged.txt') `
        -Value 'invalid stale rollback slot' -Encoding UTF8
    $invalidSlotFailure = $null
    try {
        Initialize-SswViewerMachineRollbackSlot -InstallDirectory $invalidSlotInstall |
            Out-Null
    }
    catch {
        $invalidSlotFailure = $_.Exception.Message
    }
    $invalidSlotActivePackage = Get-SswValidatedViewerPackage `
        -Directory $invalidSlotInstall `
        -ManifestPath (Join-Path $invalidSlotInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        -not [string]::IsNullOrWhiteSpace([string]$invalidSlotFailure) -and
        [string]$invalidSlotActivePackage.Manifest.version -ceq '2.0.0-test' -and
        (Test-Path -LiteralPath (Join-Path $invalidSlot 'damaged.txt') -PathType Leaf)
    ) -Message 'An invalid stale slot did not fail closed while preserving active V2 and evidence.'

    $missingInstall = Join-Path $testRoot 'missing-active\Viewer'
    $missingSlot = "$missingInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $missingSlot
    Initialize-SswViewerMachineRollbackSlot -InstallDirectory $missingInstall | Out-Null
    $missingRestored = Get-SswValidatedViewerPackage -Directory $missingInstall `
        -ManifestPath (Join-Path $missingInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        [string]$missingRestored.Manifest.version -ceq '1.0.0-test' -and
        -not (Test-Path -LiteralPath $missingSlot)
    ) -Message 'A missing active install did not restore its validated rollback slot.'

    $damagedInstall = Join-Path $testRoot 'machine-rollback\Viewer'
    $damagedSlot = "$damagedInstall.__rollback"
    New-Item -ItemType Directory -Path $damagedInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $damagedInstall 'damaged.txt') `
        -Value 'corrupt current package' -Encoding UTF8
    Copy-ViewerPackageFixture -Source $validFixture -Destination $damagedSlot

    Initialize-SswViewerMachineRollbackSlot -InstallDirectory $damagedInstall | Out-Null
    $restoredPackage = Get-SswValidatedViewerPackage -Directory $damagedInstall `
        -ManifestPath (Join-Path $damagedInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $restoredPackage.ManifestSha256 -eq $fixtureSlotPackage.ManifestSha256
    ) -Message 'The restored package does not match the validated rollback fixture.'
    Assert-ViewerInstallerTest -Condition (-not (Test-Path -LiteralPath $damagedSlot)) `
        -Message 'The restored rollback slot unexpectedly remains beside the active install.'

    $corruptRollbackInstall = Join-Path $testRoot 'marker-corrupt-active\Viewer'
    $corruptRollbackSlot = "$corruptRollbackInstall.__rollback"
    New-Item -ItemType Directory -Path $corruptRollbackInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $corruptRollbackInstall 'damaged.txt') `
        -Value 'synthetic EDR-corrupted active Viewer' -Encoding UTF8
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $corruptRollbackSlot
    $corruptRollbackPackage = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $corruptRollbackSlot
    $corruptTransactionId = ('c' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $corruptRollbackInstall `
        -TransactionId $corruptTransactionId `
        -ActiveManifestSha256 $version2Package.ManifestSha256 `
        -RollbackManifestSha256 $corruptRollbackPackage.ManifestSha256
    Confirm-SswViewerRollbackTransaction -InstallDirectory $corruptRollbackInstall `
        -TransactionId $corruptTransactionId `
        -ExpectedActiveManifestSha256 $version2Package.ManifestSha256
    $corruptRecovery = Invoke-SswViewerMachineRollbackCore `
        -InstallDirectory $corruptRollbackInstall
    Remove-SswViewerRollbackTransaction -InstallDirectory $corruptRollbackInstall `
        -TransactionId $corruptTransactionId `
        -ExpectedActiveManifestSha256 $version2Package.ManifestSha256
    $corruptRestored = Get-SswValidatedViewerPackage `
        -Directory $corruptRollbackInstall `
        -ManifestPath (Join-Path $corruptRollbackInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $corruptRecovery -ceq 'PREVIOUS_VIEWER_RESTORED' -and
        [string]$corruptRestored.Manifest.version -ceq '1.0.0-test' -and
        -not (Test-Path -LiteralPath $corruptRollbackSlot)
    ) -Message 'A matching transaction could not restore a validated slot over a corrupt active Viewer.'

    $missingRollbackInstall = Join-Path $testRoot 'marker-missing-active\Viewer'
    $missingRollbackSlot = "$missingRollbackInstall.__rollback"
    Copy-ViewerPackageFixture -Source $version1Fixture -Destination $missingRollbackSlot
    $missingRollbackPackage = Get-SswValidatedViewerRollbackPackage `
        -RollbackSlot $missingRollbackSlot
    $missingTransactionId = ('d' * 32)
    Write-SswViewerRollbackTransaction -InstallDirectory $missingRollbackInstall `
        -TransactionId $missingTransactionId `
        -ActiveManifestSha256 $version2Package.ManifestSha256 `
        -RollbackManifestSha256 $missingRollbackPackage.ManifestSha256
    Confirm-SswViewerRollbackTransaction -InstallDirectory $missingRollbackInstall `
        -TransactionId $missingTransactionId `
        -ExpectedActiveManifestSha256 $version2Package.ManifestSha256
    $missingRecovery = Invoke-SswViewerMachineRollbackCore `
        -InstallDirectory $missingRollbackInstall
    Remove-SswViewerRollbackTransaction -InstallDirectory $missingRollbackInstall `
        -TransactionId $missingTransactionId `
        -ExpectedActiveManifestSha256 $version2Package.ManifestSha256
    $missingMarkerRestored = Get-SswValidatedViewerPackage `
        -Directory $missingRollbackInstall `
        -ManifestPath (Join-Path $missingRollbackInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $missingRecovery -ceq 'PREVIOUS_VIEWER_RESTORED' -and
        [string]$missingMarkerRestored.Manifest.version -ceq '1.0.0-test' -and
        -not (Test-Path -LiteralPath $missingRollbackSlot)
    ) -Message 'A matching transaction could not restore a validated slot when active Viewer was missing.'

    $malformedMarkerInstall = Join-Path $testRoot 'malformed-marker\Viewer'
    New-Item -ItemType Directory -Path (Split-Path $malformedMarkerInstall -Parent) `
        -Force | Out-Null
    $malformedMarkerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $malformedMarkerInstall
    [IO.File]::WriteAllText(
        $malformedMarkerPath,
        'null',
        (New-Object Text.UTF8Encoding($false)))
    $malformedMarkerFailure = $null
    try {
        Read-SswViewerRollbackTransaction -InstallDirectory $malformedMarkerInstall |
            Out-Null
    }
    catch {
        $malformedMarkerFailure = $_.Exception.Message
    }
    Assert-ViewerInstallerTest -Condition (
        [string]$malformedMarkerFailure -like 'VIEWER_ROLLBACK_TRANSACTION_INVALID:*' -and
        (Test-Path -LiteralPath $malformedMarkerPath -PathType Leaf)
    ) -Message 'A malformed rollback marker was accepted or destructively removed.'

    $invalidVersionMarkerInstall = Join-Path $testRoot 'invalid-version-marker\Viewer'
    New-Item -ItemType Directory -Path (Split-Path $invalidVersionMarkerInstall -Parent) `
        -Force | Out-Null
    $invalidVersionMarkerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $invalidVersionMarkerInstall
    $invalidVersionMarker = [ordered]@{
        formatVersion = 'not-an-int'
        product = 'SamsungSwitchWatch'
        operation = 'viewer-install-rollback'
        transactionId = ('f' * 32)
        activeManifestSha256 = ('1' * 64)
        rollbackManifestSha256 = $null
    } | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText(
        $invalidVersionMarkerPath,
        $invalidVersionMarker,
        (New-Object Text.UTF8Encoding($false)))
    $invalidVersionFailure = $null
    try {
        Read-SswViewerRollbackTransaction `
            -InstallDirectory $invalidVersionMarkerInstall | Out-Null
    }
    catch {
        $invalidVersionFailure = $_.Exception.Message
    }
    Assert-ViewerInstallerTest -Condition (
        [string]$invalidVersionFailure -like
            'VIEWER_ROLLBACK_TRANSACTION_INVALID:*' -and
        (Test-Path -LiteralPath $invalidVersionMarkerPath -PathType Leaf)
    ) -Message 'An invalid marker formatVersion leaked an unstable exception or was removed.'

    $partialInstall = Join-Path $testRoot 'fresh-partial\Viewer'
    New-Item -ItemType Directory -Path $partialInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $partialInstall 'partial.txt') `
        -Value 'partial first install' -Encoding UTF8
    $partialResult = Invoke-SswViewerMachineRollbackCore -InstallDirectory $partialInstall
    Assert-ViewerInstallerTest -Condition (
        $partialResult -ceq 'PARTIAL_INSTALL_REMOVED' -and
        -not (Test-Path -LiteralPath $partialInstall)
    ) -Message 'A damaged first install without a rollback slot was not removed safely.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-SswStep 'Viewer installer package and smoke contract passed'
