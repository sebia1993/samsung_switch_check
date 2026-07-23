param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceCommit
)

. (Join-Path $PSScriptRoot 'common.ps1')

$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contractRoot = Join-Path $repoRoot "artifacts\package-contract\$Version"
Assert-SswChildPath -Parent $repoRoot -Child $contractRoot
if (-not (Test-Path -LiteralPath $release -PathType Container)) { throw "Release directory is missing: $release" }

$agentZipName = "SamsungSwitchWatch-Agent-$Version-win-x64.zip"
$viewerZipName = "SamsungSwitchWatch-Viewer-$Version-win-x64.zip"
$expectedRootNames = @(
    $agentZipName,
    $viewerZipName,
    'BUILD-MANIFEST.json',
    'SBOM.spdx.json',
    'SBOM.cdx.json',
    'SHA256SUMS.txt'
) | Sort-Object
$actualRootNames = @(Get-ChildItem -LiteralPath $release -File | ForEach-Object { $_.Name } | Sort-Object)
if (($actualRootNames -join '|') -ne ($expectedRootNames -join '|')) {
    throw "Internal release artifact set must contain exactly six files. actual=$($actualRootNames -join ', ')"
}

$hashPath = Join-Path $release 'SHA256SUMS.txt'
$declaredHashes = @{}
foreach ($line in Get-Content -LiteralPath $hashPath -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}([^\\/]+)$') { throw "Invalid SHA256SUMS line: $line" }
    if ($declaredHashes.ContainsKey($Matches[2])) { throw "Duplicate SHA256SUMS name: $($Matches[2])" }
    $declaredHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
$hashedNames = @($agentZipName, $viewerZipName, 'BUILD-MANIFEST.json', 'SBOM.spdx.json', 'SBOM.cdx.json')
if ($declaredHashes.Count -ne $hashedNames.Count) { throw 'SHA256SUMS must contain exactly five entries.' }
foreach ($name in $hashedNames) {
    $path = Join-Path $release $name
    if (-not $declaredHashes.ContainsKey($name) -or
        $declaredHashes[$name] -ne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "Release digest mismatch: $name"
    }
}

$rootManifestPath = Join-Path $release 'BUILD-MANIFEST.json'
try { $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "Root manifest is invalid: $($_.Exception.Message)" }
if ($rootManifest.manifestVersion -ne 1 -or $rootManifest.version -ne $Version -or
    [string]$rootManifest.sourceCommit -ne $ExpectedSourceCommit.ToLowerInvariant() -or
    $rootManifest.dotnetSdk -ne '10.0.302') {
    throw 'Root manifest version, commit, or SDK does not match the release contract.'
}
if ($rootManifest.signing.status -eq 'unsigned-poc' -and $Version -notmatch '-poc(?:[.-]|$)') {
    throw 'Unsigned non-POC releases are forbidden.'
}
$rootArtifactNames = @($rootManifest.artifacts | ForEach-Object { [string]$_.name } | Sort-Object)
$expectedManifestArtifacts = @($agentZipName, $viewerZipName, 'SBOM.spdx.json', 'SBOM.cdx.json') | Sort-Object
if (($rootArtifactNames -join '|') -ne ($expectedManifestArtifacts -join '|')) {
    throw 'Root manifest must describe the two ZIPs and two SBOM files only.'
}
foreach ($artifact in @($rootManifest.artifacts)) {
    $path = Join-Path $release ([string]$artifact.name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        ([string]$artifact.sha256).ToLowerInvariant()) {
        throw "Root manifest digest mismatch: $($artifact.name)"
    }
}

try { $spdx = Get-Content -LiteralPath (Join-Path $release 'SBOM.spdx.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "SPDX SBOM is invalid: $($_.Exception.Message)" }
try { $cyclone = Get-Content -LiteralPath (Join-Path $release 'SBOM.cdx.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "CycloneDX SBOM is invalid: $($_.Exception.Message)" }
if ($spdx.spdxVersion -ne 'SPDX-2.3' -or @($spdx.packages).Count -lt 1) { throw 'SPDX 2.3 contract failed.' }
if ($cyclone.bomFormat -ne 'CycloneDX' -or $cyclone.specVersion -ne '1.6' -or
    @($cyclone.components).Count -lt 1) { throw 'CycloneDX 1.6 contract failed.' }

if (Test-Path -LiteralPath $contractRoot) { Remove-Item -LiteralPath $contractRoot -Recurse -Force }
New-Item -ItemType Directory -Path $contractRoot -Force | Out-Null

try {
    $releaseNotesName = "RELEASE_NOTES_$($Version.Replace('-', '_').ToUpperInvariant())_KO.md"
    $packages = @(
        [pscustomobject]@{
            Name = 'Agent'
            ZipName = $agentZipName
            Exe = 'SamsungSwitchWatch.Agent.exe'
            Required = @(
                'SamsungSwitchWatch.Agent.exe',
                'Install-or-Update-Agent.cmd',
                'common.ps1',
                'install-agent.ps1',
                'uninstall-agent.ps1',
                'diagnose-agent.ps1',
                'INSTALL_KO.md',
                'SamsungSwitchWatch_User_Manual_KO.pdf',
                $releaseNotesName,
                'BUILD-MANIFEST.json',
                'SBOM.spdx.json',
                'SBOM.cdx.json'
            )
            Forbidden = @(
                'set-switch-credential.ps1',
                'set-viewer-access.ps1',
                'switches.example.json',
                'install-agent-background.ps1',
                'run-agent-background.ps1',
                'uninstall-agent-background.ps1',
                'appsettings.json',
                'appsettings.Production.json',
                'appsettings.Development.json',
                'RELEASE_PROCESS_KO.md',
                'SamsungSwitchWatch_User_Manual_KO.docx'
            )
        },
        [pscustomobject]@{
            Name = 'Viewer'
            ZipName = $viewerZipName
            Exe = 'SamsungSwitchWatch.Viewer.exe'
            Required = @(
                'SamsungSwitchWatch.Viewer.exe',
                'D3DCompiler_47_cor3.dll',
                'PenImc_cor3.dll',
                'PresentationNative_cor3.dll',
                'vcruntime140_cor3.dll',
                'wpfgfx_cor3.dll',
                'Install-or-Update-Viewer.cmd',
                'common.ps1',
                'install-viewer.ps1',
                'uninstall-viewer.ps1',
                'INSTALL_KO.md',
                'SamsungSwitchWatch_User_Manual_KO.pdf',
                $releaseNotesName,
                'BUILD-MANIFEST.json',
                'SBOM.spdx.json',
                'SBOM.cdx.json'
            )
            Forbidden = @('RELEASE_PROCESS_KO.md', 'SamsungSwitchWatch_User_Manual_KO.docx')
        }
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in $packages) {
        Write-SswStep "$($package.Name) ZIP contract"
        $zipPath = Join-Path $release $package.ZipName
        $expanded = Join-Path $contractRoot $package.Name
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $prefix = [IO.Path]::GetFullPath($expanded).TrimEnd('\') + '\'
            foreach ($entry in $archive.Entries) {
                $target = [IO.Path]::GetFullPath((Join-Path $expanded $entry.FullName.Replace('/', '\')))
                if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "$($package.Name) ZIP contains a path traversal entry: $($entry.FullName)"
                }
            }
        }
        finally { $archive.Dispose() }
        Expand-Archive -LiteralPath $zipPath -DestinationPath $expanded -Force

        foreach ($name in $package.Required) {
            if (-not (Test-Path -LiteralPath (Join-Path $expanded $name) -PathType Leaf)) {
                throw "$($package.Name) ZIP is missing: $name"
            }
        }
        foreach ($name in $package.Forbidden) {
            if (Test-Path -LiteralPath (Join-Path $expanded $name)) {
                throw "$($package.Name) ZIP contains forbidden material: $name"
            }
        }
        $actualPackageNames = @(Get-ChildItem -LiteralPath $expanded -File |
            ForEach-Object { $_.Name } | Sort-Object)
        $expectedPackageNames = @($package.Required | Sort-Object)
        if (($actualPackageNames -join '|') -ne ($expectedPackageNames -join '|')) {
            throw "$($package.Name) ZIP contains an undocumented or missing file. actual=$($actualPackageNames -join ', ')"
        }

        $manualPdf = Join-Path $expanded 'SamsungSwitchWatch_User_Manual_KO.pdf'
        $manualBytes = [IO.File]::ReadAllBytes($manualPdf)
        if ($manualBytes.Length -lt 1024 -or
            [Text.Encoding]::ASCII.GetString($manualBytes, 0, 5) -ne '%PDF-') {
            throw "$($package.Name) ZIP user manual is not a valid non-empty PDF."
        }

        $notes = @(Get-ChildItem -LiteralPath $expanded -Filter 'RELEASE_NOTES_*_KO.md' -File)
        if ($notes.Count -ne 1 -or $notes[0].Name -ne $releaseNotesName) {
            throw "$($package.Name) ZIP must contain only the exact release notes: $releaseNotesName"
        }

        try {
            $manifest = Get-Content -LiteralPath (Join-Path $expanded 'BUILD-MANIFEST.json') `
                -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch { throw "$($package.Name) package manifest is invalid: $($_.Exception.Message)" }
        if ($manifest.manifestVersion -ne 1 -or $manifest.packageKind -ne $package.Name -or
            $manifest.version -ne $Version -or $manifest.sourceCommit -ne $rootManifest.sourceCommit -or
            $manifest.executable.name -ne $package.Exe) {
            throw "$($package.Name) package manifest identity failed."
        }
        $payloadNames = @(Get-ChildItem -LiteralPath $expanded -File |
            Where-Object { $_.Name -ne 'BUILD-MANIFEST.json' } | ForEach-Object { $_.Name } | Sort-Object)
        $declaredNames = @($manifest.files | ForEach-Object { [string]$_.name } | Sort-Object)
        if (($payloadNames -join '|') -ne ($declaredNames -join '|')) {
            throw "$($package.Name) package manifest file list differs from ZIP contents."
        }
        foreach ($file in @($manifest.files)) {
            $name = [string]$file.name
            if ([IO.Path]::GetFileName($name) -ne $name) { throw "Unsafe manifest name: $name" }
            $path = Join-Path $expanded $name
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne
                ([string]$file.sha256).ToLowerInvariant()) {
                throw "$($package.Name) package digest mismatch: $name"
            }
        }

        $sensitive = @(Get-ChildItem -LiteralPath $expanded -File | Where-Object {
            $_.Name -match '(?i)(credential|password|secret|token|\.pfx$|\.p12$|switchwatch\.db)'
        })
        if ($sensitive.Count -gt 0) { throw "$($package.Name) ZIP contains a runtime secret or database file." }

        foreach ($script in Get-ChildItem -LiteralPath $expanded -Filter '*.ps1' -File) {
            $tokens = $null
            $errors = $null
            [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
            if ($errors) { throw "$($package.Name) PowerShell parse failed: $($script.Name)" }
        }

        if ($package.Name -eq 'Agent') {
            foreach ($entry in Get-ChildItem -LiteralPath $expanded -File) {
                if ($entry.Name -match '(?i)background' -or $entry.Name -match '^appsettings.*\.json$') {
                    throw "Public Agent ZIP is not service-only: $($entry.Name)"
                }
            }
            foreach ($document in Get-ChildItem -LiteralPath $expanded -Filter '*.md' -File) {
                $documentText = Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
                if ($documentText -match '(?i)install-agent-background|run-agent-background|--background') {
                    throw "Public Agent documentation advertises a non-service runtime: $($document.Name)"
                }
            }
            $installer = Get-Content -LiteralPath (Join-Path $expanded 'install-agent.ps1') -Raw -Encoding UTF8
            foreach ($requiredText in @(
                'https://0.0.0.0:18443',
                '[string[]]$ClientManagementCidrs',
                '[string[]]$AllowedTargetCidrs',
                '--service',
                'Invoke-SswLocalHealthProbe -Port $httpsPort -TimeoutSeconds 60 -UseHttps',
                'Restore-SswAgentFirewallSnapshots',
                'receiptVersion = 3',
                'Stop and unregister exact owned current-user Agent task',
                'Register-ScheduledTask -TaskName $legacyBackgroundTaskName',
                'legacyBackgroundTaskMigrated'
            )) {
                if (-not $installer.Contains($requiredText)) { throw "Agent install contract is missing: $requiredText" }
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $contractRoot) { Remove-Item -LiteralPath $contractRoot -Recurse -Force }
}

Write-SswStep 'Release package contract passed'
