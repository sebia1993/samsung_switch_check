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
if (-not (Test-Path -LiteralPath $release -PathType Container)) { throw "릴리스 폴더를 찾지 못했습니다: $release" }

$agentZip = Join-Path $release "SamsungSwitchWatch-Agent-$Version-win-x64.zip"
$viewerZip = Join-Path $release "SamsungSwitchWatch-Viewer-$Version-win-x64.zip"
$hashFile = Join-Path $release 'SHA256SUMS.txt'
$rootManifestPath = Join-Path $release 'BUILD-MANIFEST.json'
$spdxPath = Join-Path $release 'SBOM.spdx.json'
$cyclonePath = Join-Path $release 'SBOM.cdx.json'
$releaseNotesName = "RELEASE_NOTES_$($Version.Replace('-', '_').ToUpperInvariant())_KO.md"
$hashedReleaseFiles = @($agentZip, $viewerZip, $rootManifestPath, $spdxPath, $cyclonePath)
foreach ($required in @($hashedReleaseFiles + $hashFile)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "필수 릴리스 파일이 없습니다: $required" }
}
$expectedReleaseNames = @($hashedReleaseFiles + $hashFile | ForEach-Object { Split-Path -Leaf $_ } | Sort-Object)
$actualReleaseNames = @(Get-ChildItem -LiteralPath $release -File | ForEach-Object { $_.Name } | Sort-Object)
if (($actualReleaseNames -join '|') -ne ($expectedReleaseNames -join '|')) {
    throw "릴리스 폴더의 파일 이름 집합이 6개 패키지 계약과 일치하지 않습니다. actual=$($actualReleaseNames -join ', ')"
}

Write-SswStep 'SHA-256 파일 계약 검사'
$declaredHashes = @{}
foreach ($line in Get-Content -LiteralPath $hashFile -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') { throw "잘못된 SHA256SUMS 줄입니다: $line" }
    if ([IO.Path]::GetFileName($Matches[2]) -ne $Matches[2] -or $declaredHashes.ContainsKey($Matches[2])) {
        throw "SHA256SUMS 파일 이름이 안전하지 않거나 중복되었습니다: $($Matches[2])"
    }
    $declaredHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($releaseFile in $hashedReleaseFiles) {
    $name = Split-Path -Leaf $releaseFile
    if (-not $declaredHashes.ContainsKey($name)) { throw "SHA256SUMS에 파일이 없습니다: $name" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseFile).Hash.ToLowerInvariant()
    if ($declaredHashes[$name] -ne $actual) { throw "SHA-256 불일치: $name" }
}
if ($declaredHashes.Count -ne $hashedReleaseFiles.Count) { throw 'SHA256SUMS 항목 수가 릴리스 계약과 일치하지 않습니다.' }

try { $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "루트 BUILD-MANIFEST.json을 읽지 못했습니다: $($_.Exception.Message)" }
if ($rootManifest.manifestVersion -ne 1 -or $rootManifest.version -ne $Version -or
    $rootManifest.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $rootManifest.dotnetSdk -ne '10.0.302') {
    throw '루트 빌드 매니페스트의 버전/커밋/SDK 계약이 올바르지 않습니다.'
}
if ([string]$rootManifest.sourceCommit -ne $ExpectedSourceCommit.ToLowerInvariant()) {
    throw '루트 빌드 매니페스트의 소스 커밋이 기대한 워크플로 커밋과 다릅니다.'
}
if ($rootManifest.signing.status -eq 'unsigned-poc' -and $Version -notmatch '-poc(?:[.-]|$)') {
    throw '서명되지 않은 비 POC 릴리스는 허용하지 않습니다.'
}
foreach ($artifact in @($rootManifest.artifacts)) {
    $artifactPath = Join-Path $release ([string]$artifact.name)
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "루트 매니페스트 산출물이 없습니다: $($artifact.name)" }
    if ((Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$artifact.sha256).ToLowerInvariant()) {
        throw "루트 매니페스트 SHA-256 불일치: $($artifact.name)"
    }
}
if (@($rootManifest.artifacts).Count -ne 4) { throw '루트 빌드 매니페스트에는 ZIP 2개와 SBOM 2개가 있어야 합니다.' }
$rootArtifactNames = @($rootManifest.artifacts | ForEach-Object { [string]$_.name } | Sort-Object)
$expectedArtifactNames = @(@($agentZip, $viewerZip, $spdxPath, $cyclonePath) | ForEach-Object { Split-Path -Leaf $_ } | Sort-Object)
if (($rootArtifactNames -join '|') -ne ($expectedArtifactNames -join '|')) { throw '루트 빌드 매니페스트의 산출물 이름 집합이 올바르지 않습니다.' }

try { $spdx = Get-Content -LiteralPath $spdxPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "SPDX SBOM을 읽지 못했습니다: $($_.Exception.Message)" }
try { $cyclone = Get-Content -LiteralPath $cyclonePath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "CycloneDX SBOM을 읽지 못했습니다: $($_.Exception.Message)" }
if ($spdx.spdxVersion -ne 'SPDX-2.3' -or $spdx.SPDXID -ne 'SPDXRef-DOCUMENT' -or @($spdx.packages).Count -lt 1) {
    throw 'SPDX 2.3 SBOM 최소 계약이 올바르지 않습니다.'
}
if ($cyclone.bomFormat -ne 'CycloneDX' -or $cyclone.specVersion -ne '1.6' -or @($cyclone.components).Count -lt 1) {
    throw 'CycloneDX 1.6 SBOM 최소 계약이 올바르지 않습니다.'
}

if (Test-Path -LiteralPath $contractRoot) { Remove-Item -LiteralPath $contractRoot -Recurse -Force }
New-Item -ItemType Directory -Path $contractRoot -Force | Out-Null

try {
    Write-SswStep 'fault injection: 변조된 다운로드 digest 거부 확인'
    $tamperedZip = Join-Path $contractRoot 'tampered-agent.zip'
    Copy-Item -LiteralPath $agentZip -Destination $tamperedZip
    $tamperStream = [IO.File]::Open($tamperedZip, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $tamperStream.WriteByte(0x00) }
    finally { $tamperStream.Dispose() }
    $tamperedHash = (Get-FileHash -LiteralPath $tamperedZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $agentName = Split-Path -Leaf $agentZip
    if ($tamperedHash -eq $declaredHashes[$agentName]) { throw 'fault injection이 변조된 ZIP을 탐지하지 못했습니다.' }
    Remove-Item -LiteralPath $tamperedZip -Force

    $packages = @(
        [pscustomobject]@{
            Name = 'Agent'
            Zip = $agentZip
            Exe = 'SamsungSwitchWatch.Agent.exe'
            Installer = 'install-agent.ps1'
            Required = @('common.ps1', 'install-agent.ps1', 'uninstall-agent.ps1', 'set-switch-credential.ps1',
                'set-viewer-access.ps1', 'diagnose-agent.ps1', 'INSTALL_KO.md', 'RELEASE_PROCESS_KO.md',
                'switches.example.json', $releaseNotesName, 'BUILD-MANIFEST.json', 'SBOM.spdx.json', 'SBOM.cdx.json')
        },
        [pscustomobject]@{
            Name = 'Viewer'
            Zip = $viewerZip
            Exe = 'SamsungSwitchWatch.Viewer.exe'
            Installer = 'install-viewer.ps1'
            Required = @('common.ps1', 'install-viewer.ps1', 'uninstall-viewer.ps1',
                'INSTALL_KO.md', 'RELEASE_PROCESS_KO.md', $releaseNotesName, 'BUILD-MANIFEST.json',
                'SBOM.spdx.json', 'SBOM.cdx.json')
        }
    )

    foreach ($package in $packages) {
        Write-SswStep "$($package.Name) ZIP 구조 검사"
        $expanded = Join-Path $contractRoot $package.Name
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($package.Zip)
        try {
            $expandedPrefix = [IO.Path]::GetFullPath($expanded).TrimEnd('\') + '\'
            foreach ($entry in $archive.Entries) {
                $entryTarget = [IO.Path]::GetFullPath((Join-Path $expanded $entry.FullName.Replace('/', '\')))
                if (-not $entryTarget.StartsWith($expandedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "$($package.Name) ZIP에 경로 이탈 항목이 있습니다: $($entry.FullName)"
                }
            }
        }
        finally { $archive.Dispose() }
        Expand-Archive -LiteralPath $package.Zip -DestinationPath $expanded -Force
        if (-not (Test-Path -LiteralPath (Join-Path $expanded $package.Exe) -PathType Leaf)) {
            throw "$($package.Name) ZIP 루트에 실행 파일이 없습니다: $($package.Exe)"
        }
        foreach ($requiredName in $package.Required) {
            if (-not (Test-Path -LiteralPath (Join-Path $expanded $requiredName) -PathType Leaf)) {
                throw "$($package.Name) ZIP 필수 파일이 없습니다: $requiredName"
            }
        }
        foreach ($removedHelper in @('new-pairing-code.ps1', 'new-viewer-pairing.ps1', 'pair-viewer.ps1', 'new-agent-certificate.ps1')) {
            if (Test-Path -LiteralPath (Join-Path $expanded $removedHelper) -PathType Leaf) {
                throw "$($package.Name) ZIP에 제거된 인증/인증서 helper가 남아 있습니다: $removedHelper"
            }
        }
        $packagedReleaseNotes = @(Get-ChildItem -LiteralPath $expanded -Filter 'RELEASE_NOTES_*_KO.md' -File)
        if ($packagedReleaseNotes.Count -ne 1 -or $packagedReleaseNotes[0].Name -ne $releaseNotesName) {
            throw "$($package.Name) ZIP의 릴리스 노트가 현재 버전과 정확히 일치하지 않습니다: $releaseNotesName"
        }
        try { $packageManifest = Get-Content -LiteralPath (Join-Path $expanded 'BUILD-MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { throw "$($package.Name) BUILD-MANIFEST.json을 읽지 못했습니다: $($_.Exception.Message)" }
        if ($packageManifest.manifestVersion -ne 1 -or $packageManifest.packageKind -ne $package.Name -or
            $packageManifest.version -ne $Version -or $packageManifest.sourceCommit -ne $rootManifest.sourceCommit -or
            $packageManifest.dotnetSdk -ne '10.0.302' -or $packageManifest.executable.name -ne $package.Exe) {
            throw "$($package.Name) 빌드 매니페스트 계약이 올바르지 않습니다."
        }
        $payloadFiles = @(Get-ChildItem -LiteralPath $expanded -File | Where-Object { $_.Name -ne 'BUILD-MANIFEST.json' })
        if (@($packageManifest.files).Count -ne $payloadFiles.Count) { throw "$($package.Name) 매니페스트 파일 수가 ZIP과 다릅니다." }
        $declaredPayloadNames = @($packageManifest.files | ForEach-Object { [string]$_.name } | Sort-Object)
        $actualPayloadNames = @($payloadFiles | ForEach-Object { $_.Name } | Sort-Object)
        if (($declaredPayloadNames -join '|') -ne ($actualPayloadNames -join '|')) { throw "$($package.Name) 매니페스트 파일 이름 집합이 ZIP과 다릅니다." }
        foreach ($declaredFile in @($packageManifest.files)) {
            if ([IO.Path]::GetFileName([string]$declaredFile.name) -ne [string]$declaredFile.name) {
                throw "$($package.Name) 매니페스트 파일 이름이 안전하지 않습니다: $($declaredFile.name)"
            }
            $payloadPath = Join-Path $expanded ([string]$declaredFile.name)
            if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) { throw "$($package.Name) 매니페스트 파일이 없습니다: $($declaredFile.name)" }
            $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($payloadHash -ne ([string]$declaredFile.sha256).ToLowerInvariant()) { throw "$($package.Name) payload SHA-256 불일치: $($declaredFile.name)" }
        }
        if ((Get-FileHash -LiteralPath (Join-Path $expanded $package.Exe) -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            ([string]$packageManifest.executable.sha256).ToLowerInvariant()) {
            throw "$($package.Name) 실행 파일 SHA-256 계약이 올바르지 않습니다."
        }
        foreach ($sbomName in @('SBOM.spdx.json', 'SBOM.cdx.json')) {
            if ((Get-FileHash -LiteralPath (Join-Path $expanded $sbomName) -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath (Join-Path $release $sbomName) -Algorithm SHA256).Hash) {
                throw "$($package.Name) 내부 SBOM이 릴리스 SBOM과 다릅니다: $sbomName"
            }
        }
        if ($rootManifest.signing.status -eq 'signed') {
            foreach ($signedFile in @(Get-ChildItem -LiteralPath $expanded -File | Where-Object { $_.Extension -in @('.exe', '.ps1') })) {
                $signature = Get-AuthenticodeSignature -LiteralPath $signedFile.FullName
                if ([string]$signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $rootManifest.signing.certificateThumbprint) {
                    throw "$($package.Name) Authenticode 계약 실패: $($signedFile.Name)"
                }
            }
        }
        $installerText = Get-Content -LiteralPath (Join-Path $expanded $package.Installer) -Raw -Encoding UTF8
        if ($installerText -notmatch '\[string\]\$SourceDirectory\s*=\s*\$PSScriptRoot') {
            throw "$($package.Installer)의 기본 원본 경로는 ZIP 루트인 PSScriptRoot여야 합니다."
        }
        if ($installerText -match 'Split-Path\s+\$PSScriptRoot\s+-Parent') {
            throw "$($package.Installer)에 잘못된 상위 폴더 기본값이 남아 있습니다."
        }
        if ($installerText -notmatch '\[switch\]\$Preflight') {
            throw "$($package.Installer)에 비파괴 사전 검사 옵션이 없습니다."
        }
        $commonText = Get-Content -LiteralPath (Join-Path $expanded 'common.ps1') -Raw -Encoding UTF8
        $uninstallerName = if ($package.Name -eq 'Agent') { 'uninstall-agent.ps1' } else { 'uninstall-viewer.ps1' }
        $uninstallerText = Get-Content -LiteralPath (Join-Path $expanded $uninstallerName) -Raw -Encoding UTF8
        if ($commonText -notmatch 'function\s+Assert-SswProductPath' -or
            $installerText -notmatch 'Assert-SswProductPath\s+-Path\s+\$install' -or
            $uninstallerText -notmatch 'Assert-SswProductPath\s+-Path\s+\$install') {
            throw "$($package.Name) 설치·제거 경로의 제품 전용 경계 검사가 없습니다."
        }
        if ($package.Name -eq 'Agent') {
            if ($installerText -notmatch '\[switch\]\$Repair') { throw 'Agent 설치 스크립트에 복구 설치 옵션이 없습니다.' }
            if ($installerText -match '\[string\]\$ServiceName') { throw 'Agent 서비스 이름은 사용자 지정할 수 없어야 합니다.' }
            if ($installerText -notmatch 'Get-SswAgentServiceName') { throw 'Agent 고정 서비스 이름 계약이 없습니다.' }
            foreach ($validationPattern in @(
                '\$AgentId\s+-notmatch',
                '\$id\s+-notmatch',
                '\$credential\s+-notmatch',
                'IPAddress\]::TryParse\(\$hostAddress',
                '\$uplink\s+-notmatch'
            )) {
                if ($installerText -notmatch $validationPattern) {
                    throw "Agent 설치 전 입력 검증 계약이 없습니다: $validationPattern"
                }
            }
            foreach ($inventoryPattern in @(
                '\[ValidateSet\(''IES4224GP'',\s*''IES4028XP'',\s*''IES4226XP''\)\]',
                '\[string\]\$SwitchesJsonPath',
                '\$switchInput\.Count\s+-lt\s+1',
                'Group-Object',
                'switchInventoryHash'
            )) {
                if ($installerText -notmatch $inventoryPattern) { throw "Agent 다중 모델/인벤토리 계약이 없습니다: $inventoryPattern" }
            }
            if ($commonText -notmatch 'function\s+Set-SswRestrictedDirectoryAcl' -or
                $installerText -notmatch 'Set-SswRestrictedDirectoryAcl\s+-Path\s+\$install' -or
                $installerText -notmatch 'Set-SswRestrictedDirectoryAcl\s+-Path\s+\$data') {
                throw 'Agent 설치 폴더와 데이터 폴더의 폐쇄형 ACL 계약이 없습니다.'
            }
            if ($installerText -match 'icacls\.exe\s+\$(?:install|data)\s+/grant') {
                throw '기존 명시적 ACE를 남길 수 있는 증분 ACL 설정이 포함되어 있습니다.'
            }
            $credentialText = Get-Content -LiteralPath (Join-Path $expanded 'set-switch-credential.ps1') -Raw -Encoding UTF8
            $viewerAccessText = Get-Content -LiteralPath (Join-Path $expanded 'set-viewer-access.ps1') -Raw -Encoding UTF8
            if ($credentialText -notmatch 'Push-Location\s+-LiteralPath\s+\$install') {
                throw 'Agent 자격 증명 명령은 설치 폴더를 작업 디렉터리로 고정해야 합니다.'
            }
            foreach ($httpPattern in @(
                '\[ValidateRange\(1,\s*65535\)\]\[int\]\$HttpPort\s*=\s*18443',
                '\[ValidateCount\(1,\s*32\)\]\[string\[\]\]\$ViewerRemoteAddress',
                'http://0\.0\.0\.0:\$HttpPort',
                'receiptVersion\s*=\s*2',
                'httpPort\s*=\s*\$HttpPort',
                'viewerRemoteAddresses\s*=\s*\$viewerRemoteAddresses',
                'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=\*',
                'legacyOwnedCertificateThumbprint',
                'restore-service-environment',
                "@\('Https',\s*'PairingCodeLifetimeMinutes',\s*'TokenPepper',\s*'Tokens'\)",
                '\$installedDataDirectory\s*=\s*\[IO\.Path\]::GetFullPath',
                '\$data\s*=\s*\$installedDataDirectory',
                '\$shouldStart\s*=\s*\$Repair\s+-or',
                '\$previousServiceWasRunning\s+-and\s+-not\s+\$DoNotStart',
                'readiness \(clean environment\)',
                'Restore-SswAgentFirewallSnapshot'
            )) {
                if ($installerText -notmatch $httpPattern) { throw "Agent HTTP 마이그레이션 계약이 없습니다: $httpPattern" }
            }
            foreach ($removedPattern in @('\$HttpsPort', '\[switch\]\$SkipFirewall', '\[switch\]\$RotateCertificate', 'PairingCodeLifetimeMinutes\s*=', 'TokenPepper\s*=')) {
                if ($installerText -match $removedPattern) { throw "제거된 HTTPS/인증 설치 계약이 남아 있습니다: $removedPattern" }
            }
            foreach ($accessPattern in @(
                'ConvertTo-SswViewerRemoteAddresses',
                'Set-NetFirewallAddressFilter\s+-RemoteAddress',
                'Test-SswAgentFirewallRuleExact',
                '\[IO\.File\]::Replace\(\$temporaryReceipt',
                'Restore-SswAgentFirewallSnapshot'
            )) {
                if ($viewerAccessText -notmatch $accessPattern) { throw "Viewer 방화벽 원자 갱신 계약이 없습니다: $accessPattern" }
            }
            foreach ($commonSecurityPattern in @(
                'canonical dotted-quad',
                '\$_\s+-gt\s+255',
                'function\s+Test-SswFirewallProfileSetExact',
                'Profile\s+Domain,Private',
                "Get-Service\s+-Name\s+'MpsSvc'",
                "\.Status\s+-ne\s+'Running'",
                'Get-NetFirewallProfile\s+-Name\s+Domain,Private,Public',
                "@\('Domain',\s*'Private',\s*'Public'\)",
                "DefaultInboundAction\s+-eq\s+'Allow'",
                "AllowInboundRules\s+-eq\s+'False'",
                "AllowLocalFirewallRules\s+-eq\s+'False'",
                'Get-NetFirewallRule\s+-Enabled\s+True\s+-Direction\s+Inbound\s+-Action\s+Allow',
                'Test-SswFirewallPortOverlap',
                'Test-SswFirewallRuleMayApplyToAgent',
                'function\s+Assert-SswAgentInstallReceipt',
                '\$receiptVersion\s+-notin\s+@\(1,\s*2\)',
                'switchInventoryHash'
            )) {
                if ($commonText -notmatch $commonSecurityPattern) {
                    throw "Agent 공통 방화벽/영수증 보안 계약이 없습니다: $commonSecurityPattern"
                }
            }
            foreach ($exactFilterPattern in @(
                'RemotePort\s*=\s*\[string\]\$port\.RemotePort',
                'LocalAddress\s*=\s*@\(\$address\.LocalAddress',
                'Program\s*=\s*\[string\]\$application\.Program',
                'Service\s*=\s*\[string\]\$service\.Service',
                'InterfaceType\s*=\s*\[string\]\$interfaceType\.InterfaceType'
            )) {
                if ($commonText -notmatch $exactFilterPattern) {
                    throw "Agent 방화벽 exact 가용성 filter 계약이 없습니다: $exactFilterPattern"
                }
            }
            if ($commonText -match '\$profileText\s*=') {
                throw 'Agent 외부 인바운드 Allow 중첩 검사가 특정 프로필 규칙만 선별합니다.'
            }
            foreach ($installerSecurityPattern in @(
                'Assert-SswAgentFirewallGateReady\s+-Port\s+\$HttpPort',
                'Assert-SswAgentInstallReceipt',
                'Remove-SswOwnedAgentFirewallRuleByName',
                'Get-SswLegacyOwnedAgentCertificateThumbprints',
                'Remove-Item\s+-LiteralPath\s+\$(?:legacy)?CertificatePath'
            )) {
                if ($installerText -notmatch $installerSecurityPattern) {
                    throw "Agent 설치/Repair 보안 계약이 없습니다: $installerSecurityPattern"
                }
            }
            foreach ($accessSecurityPattern in @(
                '\$installedDataDirectory\s*=\s*\[IO\.Path\]::GetFullPath',
                '\$PSBoundParameters\.ContainsKey\(''DataDirectory''\)',
                '\$data\.Equals\(\$installedDataDirectory',
                '\$data\s*=\s*\$installedDataDirectory',
                '\$receiptPath\s*=\s*Join-Path\s+\$data\s+''install-receipt\.json''',
                'Assert-SswAgentInstallReceipt',
                'Assert-SswAgentFirewallGateReady\s+-Port\s+\$httpPort',
                'Invoke-SswBestEffortPlan',
                '\$preserveRollbackArtifacts'
            )) {
                if ($viewerAccessText -notmatch $accessSecurityPattern) {
                    throw "Viewer 허용 주소 보안 계약이 없습니다: $accessSecurityPattern"
                }
            }
            foreach ($credentialFirewallPattern in @(
                'Assert-SswCredentialStartFirewall',
                'Assert-SswAgentInstallReceipt',
                'Test-SswAgentFirewallRuleExact',
                'Assert-SswAgentFirewallGateReady'
            )) {
                if ($credentialText -notmatch $credentialFirewallPattern) {
                    throw "Agent 최초 시작 방화벽 안전 계약이 없습니다: $credentialFirewallPattern"
                }
            }
            foreach ($uninstallSecurityPattern in @(
                'Assert-SswAgentFirewallNameSafety',
                'Remove-SswOwnedAgentFirewallRuleByName',
                '\$installedDataDirectory\s*=\s*\[IO\.Path\]::GetFullPath',
                '\$PSBoundParameters\.ContainsKey\(''DataDirectory''\)',
                '\$data\.Equals\(\$installedDataDirectory',
                '\$data\s*=\s*\$installedDataDirectory',
                '\$receiptPath\s*=\s*Join-Path\s+\$data\s+''install-receipt\.json''',
                'Assert-SswAgentInstallReceipt',
                'Get-SswLegacyOwnedAgentCertificateThumbprints',
                'Remove-Item\s+-LiteralPath\s+\$(?:legacy)?CertificatePath'
            )) {
                if ($uninstallerText -notmatch $uninstallSecurityPattern) {
                    throw "Agent 제거 보안 계약이 없습니다: $uninstallSecurityPattern"
                }
            }
            foreach ($legacyCertificateHelperPattern in @(
                'function\s+Get-SswLegacyOwnedAgentCertificateThumbprints',
                'certificateOwnedByInstaller',
                'certificateStoreThumbprint',
                'previousCertificateOwnedByInstaller',
                'previousCertificateStoreThumbprint',
                '\$certificate\.FriendlyName\s+-eq\s+\$expectedFriendlyName',
                'Get-SswCertificateSha256',
                'Cert:\\LocalMachine\\My\\'
            )) {
                if ($commonText -notmatch $legacyCertificateHelperPattern) {
                    throw "v0.5 active/previous 인증서 소유권 검증 helper 계약이 없습니다: $legacyCertificateHelperPattern"
                }
            }
            if ($installerText -match 'AllowLegacyHealth' -or $credentialText -match 'AllowLegacyHealth') {
                throw 'Agent 설치와 자격 증명 검증은 v2 readiness를 구형 health 응답으로 우회할 수 없습니다.'
            }
            foreach ($credentialPattern in @(
                '\$Username\s+-match',
                '\$CredentialId\s+-notin\s+\$configuredCredentialIds',
                '\[IO\.File\]::Copy\(\$credentialPath,\s*\$credentialBackup',
                '\[IO\.File\]::Replace\(\$credentialBackup,\s*\$credentialPath',
                '\$credentialWriteAttempted\s*=\s*\$true',
                '\$rollbackSucceeded\s*=\s*\$true',
                '\$credentialUpdateValidated\s+-or\s+\$rollbackSucceeded',
                'TimeoutSeconds\s+210',
                'CREDENTIAL_ROLLBACK_FAILED'
            )) {
                if ($credentialText -notmatch $credentialPattern) {
                    throw "Agent 자격 증명 안전 갱신 계약이 없습니다: $credentialPattern"
                }
            }
        }
        $sensitiveFiles = @(Get-ChildItem -LiteralPath $expanded -Recurse -Force | Where-Object {
            $_.Extension -in @('.pfx', '.p12', '.db', '.key', '.pem')
        })
        if ($sensitiveFiles.Count -gt 0) { throw "$($package.Name) ZIP에 민감한 런타임 파일이 포함되어 있습니다." }

        $parseFailures = @()
        Get-ChildItem -LiteralPath $expanded -Filter '*.ps1' | ForEach-Object {
            $tokens = $null
            $parseErrors = $null
            [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors) { $parseFailures += $parseErrors }
        }
        if ($parseFailures.Count -gt 0) {
            $parseFailures | ForEach-Object { Write-Error ("{0}: {1}" -f $_.Extent.File, $_.Message) }
            throw "$($package.Name) ZIP PowerShell 5.1 구문 검사에 실패했습니다."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $contractRoot) { Remove-Item -LiteralPath $contractRoot -Recurse -Force }
}

Write-SswStep '릴리스 패키지 계약 검사 완료'
