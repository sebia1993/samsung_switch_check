param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$Version
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
foreach ($required in @($agentZip, $viewerZip, $hashFile)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "필수 릴리스 파일이 없습니다: $required" }
}

Write-SswStep 'SHA-256 파일 계약 검사'
$declaredHashes = @{}
foreach ($line in Get-Content -LiteralPath $hashFile -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s{2}(.+)$') { throw "잘못된 SHA256SUMS 줄입니다: $line" }
    $declaredHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($zip in @($agentZip, $viewerZip)) {
    $name = Split-Path -Leaf $zip
    if (-not $declaredHashes.ContainsKey($name)) { throw "SHA256SUMS에 파일이 없습니다: $name" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    if ($declaredHashes[$name] -ne $actual) { throw "SHA-256 불일치: $name" }
}
if ($declaredHashes.Count -ne 2) { throw 'SHA256SUMS에는 Agent와 Viewer ZIP 두 개만 있어야 합니다.' }

if (Test-Path -LiteralPath $contractRoot) { Remove-Item -LiteralPath $contractRoot -Recurse -Force }
New-Item -ItemType Directory -Path $contractRoot -Force | Out-Null

try {
    $packages = @(
        [pscustomobject]@{
            Name = 'Agent'
            Zip = $agentZip
            Exe = 'SamsungSwitchWatch.Agent.exe'
            Installer = 'install-agent.ps1'
            Required = @('common.ps1', 'install-agent.ps1', 'uninstall-agent.ps1', 'set-switch-credential.ps1',
                'new-pairing-code.ps1', 'diagnose-agent.ps1', 'INSTALL_KO.md', 'RELEASE_NOTES_0.2.0_POC_KO.md')
        },
        [pscustomobject]@{
            Name = 'Viewer'
            Zip = $viewerZip
            Exe = 'SamsungSwitchWatch.Viewer.exe'
            Installer = 'install-viewer.ps1'
            Required = @('common.ps1', 'install-viewer.ps1', 'uninstall-viewer.ps1', 'pair-viewer.ps1',
                'INSTALL_KO.md', 'RELEASE_NOTES_0.2.0_POC_KO.md')
        }
    )

    foreach ($package in $packages) {
        Write-SswStep "$($package.Name) ZIP 구조 검사"
        $expanded = Join-Path $contractRoot $package.Name
        Expand-Archive -LiteralPath $package.Zip -DestinationPath $expanded -Force
        if (-not (Test-Path -LiteralPath (Join-Path $expanded $package.Exe) -PathType Leaf)) {
            throw "$($package.Name) ZIP 루트에 실행 파일이 없습니다: $($package.Exe)"
        }
        foreach ($requiredName in $package.Required) {
            if (-not (Test-Path -LiteralPath (Join-Path $expanded $requiredName) -PathType Leaf)) {
                throw "$($package.Name) ZIP 필수 파일이 없습니다: $requiredName"
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
                '\$SwitchId\s+-notmatch',
                '\$CredentialId\s+-notmatch',
                'IPAddress\]::TryParse\(\$SwitchHost',
                '\$UplinkPort\s+-notmatch',
                'IPAddress\]::TryParse\(\$ViewerRemoteAddress'
            )) {
                if ($installerText -notmatch $validationPattern) {
                    throw "Agent 설치 전 입력 검증 계약이 없습니다: $validationPattern"
                }
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
            $pairingText = Get-Content -LiteralPath (Join-Path $expanded 'new-pairing-code.ps1') -Raw -Encoding UTF8
            if ($credentialText -notmatch 'Push-Location\s+-LiteralPath\s+\$install' -or
                $pairingText -notmatch 'Push-Location\s+-LiteralPath\s+\$install') {
                throw 'Agent 유지보수 명령은 설치 폴더를 작업 디렉터리로 고정해야 합니다.'
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
