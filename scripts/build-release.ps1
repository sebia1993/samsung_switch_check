param(
    [string]$Version = '0.5.0-poc',
    [switch]$SkipTests,
    [switch]$AllowDirty,
    [string]$SigningCertificatePath,
    [string]$SigningPasswordEnvironmentVariable = 'SSW_SIGNING_CERTIFICATE_PASSWORD',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireSigning
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "릴리스 버전 형식이 올바르지 않습니다: $Version" }
if ($RequireSigning -and [string]::IsNullOrWhiteSpace($SigningCertificatePath)) { throw '서명이 필수이지만 인증서 경로가 지정되지 않았습니다.' }
if ([string]::IsNullOrWhiteSpace($SigningCertificatePath) -and $Version -notmatch '-poc(?:[.-]|$)') {
    throw '서명되지 않은 산출물은 -poc 버전으로만 만들 수 있습니다.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'SamsungSwitchWatch.sln'
$artifacts = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$releaseRoot = Join-Path $artifacts 'release'
$dotnet = Get-SswDotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$sourceCommit = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw '소스 Git 커밋을 확인하지 못했습니다.' }
$dirtyLines = @(& git -C $repoRoot status --porcelain --untracked-files=all)
$sourceDirty = $dirtyLines.Count -gt 0
if ($sourceDirty -and -not $AllowDirty) { throw '재현 가능한 릴리스에는 깨끗한 Git 작업 트리가 필요합니다. 로컬 진단 빌드만 -AllowDirty를 사용하세요.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$Version") {
    throw "빌드 버전과 Git 태그가 일치하지 않습니다: $Version / $($env:GITHUB_REF_NAME)"
}

Assert-SswChildPath -Parent $repoRoot -Child $artifacts
if (Test-Path -LiteralPath $artifacts) {
    Write-SswStep '이전 artifacts 제거'
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot, $releaseRoot -Force | Out-Null

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'validate.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw '검증 스크립트 실행 실패' }
}

$agentProject = Join-Path $repoRoot 'src\SamsungSwitchWatch.Agent\SamsungSwitchWatch.Agent.csproj'
$viewerProject = Join-Path $repoRoot 'src\SamsungSwitchWatch.Viewer\SamsungSwitchWatch.Viewer.csproj'
$agentOut = Join-Path $publishRoot 'Agent'
$viewerOut = Join-Path $publishRoot 'Viewer'

foreach ($project in @($agentProject, $viewerProject)) {
    Write-SswStep "RID 전용 NuGet 잠금 복원: $(Split-Path -Leaf $project)"
    & $dotnet restore $project -r win-x64 --locked-mode -p:NuGetLockFilePath=packages.win-x64.lock.json
    if ($LASTEXITCODE -ne 0) { throw "win-x64 잠금 복원 실패: $project" }
}

Write-SswStep 'Agent self-contained publish'
& $dotnet publish $agentProject -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:Version=$Version `
    -p:ContinuousIntegrationBuild=true -p:NuGetLockFilePath=packages.win-x64.lock.json -o $agentOut
if ($LASTEXITCODE -ne 0) { throw 'Agent publish 실패' }

Write-SswStep 'Viewer self-contained publish'
& $dotnet publish $viewerProject -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:Version=$Version `
    -p:ContinuousIntegrationBuild=true -p:NuGetLockFilePath=packages.win-x64.lock.json -o $viewerOut
if ($LASTEXITCODE -ne 0) { throw 'Viewer publish 실패' }

$agentScripts = @('common.ps1', 'install-agent.ps1', 'uninstall-agent.ps1', 'set-switch-credential.ps1',
    'new-pairing-code.ps1', 'new-viewer-pairing.ps1', 'new-agent-certificate.ps1', 'diagnose-agent.ps1')
$viewerScripts = @('common.ps1', 'install-viewer.ps1', 'uninstall-viewer.ps1', 'pair-viewer.ps1')
foreach ($script in $agentScripts) { Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$script") -Destination $agentOut }
foreach ($script in $viewerScripts) { Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$script") -Destination $viewerOut }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL_KO.md') -Destination $agentOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL_KO.md') -Destination $viewerOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASE_PROCESS_KO.md') -Destination $agentOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASE_PROCESS_KO.md') -Destination $viewerOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\examples\switches.example.json') `
    -Destination (Join-Path $agentOut 'switches.example.json')
$releaseNotesToken = $Version.Replace('-', '_').ToUpperInvariant()
$releaseNotes = Join-Path $repoRoot "docs\RELEASE_NOTES_${releaseNotesToken}_KO.md"
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
    throw "현재 버전의 릴리스 노트가 없습니다: $releaseNotes"
}
Copy-Item -LiteralPath $releaseNotes -Destination $agentOut
Copy-Item -LiteralPath $releaseNotes -Destination $viewerOut

Write-SswStep 'SPDX 및 CycloneDX SBOM 생성'
& (Join-Path $PSScriptRoot 'new-release-sbom.ps1') -SolutionPath $solution -Version $Version `
    -SourceCommit $sourceCommit -OutputDirectory $releaseRoot -DotNetPath $dotnet
if ($LASTEXITCODE -ne 0) { throw 'SBOM 생성 실패' }
foreach ($output in @($agentOut, $viewerOut)) {
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'SBOM.spdx.json') -Destination $output
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'SBOM.cdx.json') -Destination $output
}

$signed = -not [string]::IsNullOrWhiteSpace($SigningCertificatePath)
$signingThumbprint = $null
if ($signed) {
    $certificatePath = [IO.Path]::GetFullPath($SigningCertificatePath)
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) { throw "코드 서명 인증서를 찾지 못했습니다: $certificatePath" }
    $passwordText = [Environment]::GetEnvironmentVariable($SigningPasswordEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($passwordText)) { throw "코드 서명 암호 환경 변수가 비어 있습니다: $SigningPasswordEnvironmentVariable" }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificatePath, $passwordText, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if (-not $certificate.HasPrivateKey) { throw '코드 서명 인증서에 개인 키가 없습니다.' }
        $codeSigningEku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
            ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
        if ($codeSigningEku.Count -eq 0) { throw '인증서에 Code Signing EKU가 없습니다.' }
        $signingThumbprint = $certificate.Thumbprint
        $signTargets = @(Get-ChildItem -LiteralPath $agentOut, $viewerOut -File | Where-Object { $_.Extension -in @('.exe', '.ps1') })
        foreach ($target in $signTargets) {
            $signature = Set-AuthenticodeSignature -LiteralPath $target.FullName -Certificate $certificate `
                -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
            if ([string]$signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {
                throw "Authenticode 서명 또는 타임스탬프 검증 실패: $($target.Name) / $($signature.Status)"
            }
        }
    }
    finally {
        $passwordText = $null
        $certificate.Dispose()
    }
}

function Write-PackageManifest {
    param([string]$Directory, [string]$PackageKind, [string]$ExecutableName)
    $files = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Name -ne 'BUILD-MANIFEST.json' } |
        Sort-Object Name | ForEach-Object {
            $signature = if ($_.Extension -in @('.exe', '.ps1')) { Get-AuthenticodeSignature -LiteralPath $_.FullName } else { $null }
            [ordered]@{
                name = $_.Name
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                authenticode = if ($signature) { [string]$signature.Status } else { 'not-applicable' }
            }
        })
    $executable = $files | Where-Object { $_.name -eq $ExecutableName } | Select-Object -First 1
    if (-not $executable) { throw "패키지 실행 파일을 찾지 못했습니다: $ExecutableName" }
    $versionInfo = (Get-Item -LiteralPath (Join-Path $Directory $ExecutableName)).VersionInfo
    $manifest = [ordered]@{
        manifestVersion = 1
        product = 'SamsungSwitchWatch'
        packageKind = $PackageKind
        version = $Version
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        repository = 'https://github.com/sebia1993/samsung_switch_check.git'
        runtimeIdentifier = 'win-x64'
        dotnetSdk = (& $dotnet --version).Trim()
        builtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        signing = [ordered]@{ status = if ($signed) { 'signed' } else { 'unsigned-poc' }; certificateThumbprint = $signingThumbprint; timestampUrl = if ($signed) { $TimestampUrl } else { $null } }
        executable = [ordered]@{ name = $ExecutableName; sha256 = $executable.sha256; productVersion = $versionInfo.ProductVersion }
        files = $files
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'BUILD-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
}

Write-PackageManifest -Directory $agentOut -PackageKind 'Agent' -ExecutableName 'SamsungSwitchWatch.Agent.exe'
Write-PackageManifest -Directory $viewerOut -PackageKind 'Viewer' -ExecutableName 'SamsungSwitchWatch.Viewer.exe'

$agentZip = Join-Path $releaseRoot "SamsungSwitchWatch-Agent-$Version-win-x64.zip"
$viewerZip = Join-Path $releaseRoot "SamsungSwitchWatch-Viewer-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $agentOut '*') -DestinationPath $agentZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $viewerOut '*') -DestinationPath $viewerZip -CompressionLevel Optimal

$rootFiles = @($agentZip, $viewerZip, (Join-Path $releaseRoot 'SBOM.spdx.json'), (Join-Path $releaseRoot 'SBOM.cdx.json'))
$rootManifest = [ordered]@{
    manifestVersion = 1
    product = 'SamsungSwitchWatch'
    version = $Version
    sourceCommit = $sourceCommit
    sourceDirty = $sourceDirty
    repository = 'https://github.com/sebia1993/samsung_switch_check.git'
    runtimeIdentifier = 'win-x64'
    dotnetSdk = (& $dotnet --version).Trim()
    builtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    signing = [ordered]@{ status = if ($signed) { 'signed' } else { 'unsigned-poc' }; certificateThumbprint = $signingThumbprint }
    artifacts = @($rootFiles | ForEach-Object { [ordered]@{ name = Split-Path -Leaf $_; size = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
}
$rootManifestPath = Join-Path $releaseRoot 'BUILD-MANIFEST.json'
[IO.File]::WriteAllText($rootManifestPath, ($rootManifest | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))

$hashInputs = @($rootFiles + $rootManifestPath)
$hashLines = Get-FileHash -Algorithm SHA256 -LiteralPath $hashInputs | ForEach-Object {
    '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path)
}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $hashLines, (New-Object Text.UTF8Encoding($false)))

& (Join-Path $PSScriptRoot 'test-package-contract.ps1') -ReleaseDirectory $releaseRoot -Version $Version `
    -ExpectedSourceCommit $sourceCommit
if ($LASTEXITCODE -ne 0) { throw '릴리스 패키지 계약 검사 실패' }

Write-SswStep "릴리스 완료: $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot | Select-Object Name, Length, LastWriteTime
