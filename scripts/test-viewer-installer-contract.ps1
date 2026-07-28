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
    param([Parameter(Mandatory = $true)][string]$Path)

    New-Item -ItemType Directory -Path $Path | Out-Null
    $executablePath = Join-Path $Path 'SamsungSwitchWatch.Viewer.exe'
    [IO.File]::WriteAllText(
        $executablePath,
        'deterministic-viewer-installer-contract-fixture',
        (New-Object Text.UTF8Encoding($false)))
    $executable = Get-Item -LiteralPath $executablePath
    $hash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        manifestVersion = 1
        product = 'SamsungSwitchWatch'
        packageKind = 'Viewer'
        version = '0.0.0-test'
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
            productVersion = '0.0.0-test'
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
        '"$resolvedInstall.__rollback"',
        'VIEWER_MACHINE_ROLLBACK_INCOMPLETE',
        'Recovery: ROLLBACK_INCOMPLETE',
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
        'Assert-SswTrustedDirectoryRootOwner -Path $machineRollbackSlot',
        "Name = 'remove-machine-rollback-slot'",
        '$uninstallState.ActiveProgramRemoved',
        'VIEWER_UNINSTALL_ROLLBACK_PRESERVED',
        'VIEWER_UNINSTALL_SHORTCUTS_PRESERVED',
        'VIEWER_UNINSTALL_SETTINGS_PRESERVED'
    )) {
        Assert-ViewerInstallerTest -Condition $uninstallerText.Contains($needle) `
            -Message "Viewer uninstaller ownership contract is missing: $needle"
    }

    $swapIndex = $installerText.IndexOf('$installSwapped = $true')
    $installedValidationIndex = $installerText.IndexOf(
        'Get-SswValidatedViewerPackage -Directory $install', $swapIndex)
    $smokeIndex = $installerText.IndexOf(
        'Invoke-SswViewerSelfCheck -ViewerExecutable $viewerExe', $installedValidationIndex)
    $commitIndex = $installerText.IndexOf("-Stage 'completed' -Status 'succeeded'", $smokeIndex)
    $committedFlagIndex = $installerText.IndexOf('$transactionCommitted = $true', $commitIndex)
    $normalStartIndex = $installerText.IndexOf(
        'if (-not $MachinePhase -and -not $DoNotStart)', $committedFlagIndex)
    Assert-ViewerInstallerTest -Condition (
        $swapIndex -ge 0 -and
        $installedValidationIndex -gt $swapIndex -and
        $smokeIndex -gt $installedValidationIndex -and
        $commitIndex -gt $smokeIndex -and
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
    $coreStart = $installerText.IndexOf(
        'function Invoke-SswViewerMachineRollbackCore', $initializeStart)
    $initializeText = $installerText.Substring(
        $initializeStart,
        $coreStart - $initializeStart)
    Assert-ViewerInstallerTest -Condition $initializeText.Contains(
        'Invoke-SswViewerMachineRollbackCore -InstallDirectory $InstallDirectory') `
        -Message 'A leftover rollback slot and current install must invoke core recovery.'
    Assert-ViewerInstallerTest -Condition $initializeText.Contains(
        "'PREVIOUS_VIEWER_RESTORED'") `
        -Message 'Pre-update rollback recovery must require PREVIOUS_VIEWER_RESTORED.'
    Assert-ViewerInstallerTest -Condition (-not $initializeText.Contains(
        'Remove-Item -LiteralPath $rollbackSlot')) `
        -Message 'Pre-update initialization must never delete a working rollback slot.'

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

    # EDR 격리나 손상으로 현재 실행 파일/manifest가 사라져도 검증된 slot은
    # 현재 패키지 검증에 막히지 않고 복원되어야 한다.
    function Assert-SswTrustedDirectoryRootOwner {
        param([Parameter(Mandatory = $true)][string]$Path)
        return 'S-1-5-32-544'
    }
    $damagedInstall = Join-Path $testRoot 'machine-rollback\Viewer'
    $damagedSlot = "$damagedInstall.__rollback"
    New-Item -ItemType Directory -Path $damagedInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $damagedInstall 'damaged.txt') `
        -Value 'corrupt current package' -Encoding UTF8
    Copy-ViewerPackageFixture -Source $validFixture -Destination $damagedSlot

    $rollbackResult = Invoke-SswViewerMachineRollbackCore -InstallDirectory $damagedInstall
    Assert-ViewerInstallerTest -Condition ($rollbackResult -ceq 'PREVIOUS_VIEWER_RESTORED') `
        -Message 'A damaged current Viewer install blocked restoration of the validated rollback slot.'
    $restoredPackage = Get-SswValidatedViewerPackage -Directory $damagedInstall `
        -ManifestPath (Join-Path $damagedInstall 'BUILD-MANIFEST.json')
    Assert-ViewerInstallerTest -Condition (
        $restoredPackage.ManifestSha256 -eq $fixtureSlotPackage.ManifestSha256
    ) -Message 'The restored package does not match the validated rollback fixture.'
    Assert-ViewerInstallerTest -Condition (-not (Test-Path -LiteralPath $damagedSlot)) `
        -Message 'The restored rollback slot unexpectedly remains beside the active install.'

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
