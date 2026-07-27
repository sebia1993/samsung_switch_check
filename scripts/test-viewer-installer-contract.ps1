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
        & $Installer -SourceDirectory $Source -InstallDirectory $InstallDirectory `
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
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-viewer-installer-$testId"
$validFixture = Join-Path $testRoot 'valid'
$installDirectory = Join-Path $env:LOCALAPPDATA (
    "Programs\SamsungSwitchWatch\Viewer\contract-$testId")

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    New-ViewerPackageFixture -Path $validFixture

    & $installer -SourceDirectory $validFixture -InstallDirectory $installDirectory `
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
        '[Environment]::Is64BitOperatingSystem',
        '$stagedPackage.ManifestSha256 -ne $sourcePackage.ManifestSha256',
        'foreach ($file in @($sourcePackage.Files))',
        "-ArgumentList '--install-smoke-check'",
        '$smokeProcess.WaitForExit(20000)',
        "'VIEWER_SELF_CHECK_START_FAILED'",
        "'VIEWER_SELF_CHECK_WAIT_FAILED'",
        "'VIEWER_SELF_CHECK_TIMEOUT'",
        "'VIEWER_SELF_CHECK_EXITED_NONZERO'",
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

    $smokeIndex = $installerText.IndexOf("-ArgumentList '--install-smoke-check'")
    $commitIndex = $installerText.IndexOf("-Stage 'completed' -Status 'succeeded'", $smokeIndex)
    $committedFlagIndex = $installerText.IndexOf('$transactionCommitted = $true', $commitIndex)
    $normalStartIndex = $installerText.IndexOf('if (-not $DoNotStart)', $committedFlagIndex)
    Assert-ViewerInstallerTest -Condition (
        $smokeIndex -ge 0 -and
        $commitIndex -gt $smokeIndex -and
        $committedFlagIndex -gt $commitIndex -and
        $normalStartIndex -gt $committedFlagIndex
    ) -Message 'The normal Viewer must start only after the smoke check and durable commit.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-SswStep 'Viewer installer package and smoke contract passed'
