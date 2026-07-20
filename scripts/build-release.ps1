param(
    [string]$Version = '0.2.0-poc',
    [switch]$SkipTests
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "릴리스 버전 형식이 올바르지 않습니다: $Version"
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$releaseRoot = Join-Path $artifacts 'release'
$dotnet = Get-SswDotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Assert-SswChildPath -Parent $repoRoot -Child $artifacts
if (Test-Path -LiteralPath $artifacts) {
    Write-SswStep '이전 artifacts 제거'
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot, $releaseRoot -Force | Out-Null

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'validate.ps1') -Configuration Release
}

$agentProject = Join-Path $repoRoot 'src\SamsungSwitchWatch.Agent\SamsungSwitchWatch.Agent.csproj'
$viewerProject = Join-Path $repoRoot 'src\SamsungSwitchWatch.Viewer\SamsungSwitchWatch.Viewer.csproj'
$agentOut = Join-Path $publishRoot 'Agent'
$viewerOut = Join-Path $publishRoot 'Viewer'

Write-SswStep 'Agent self-contained publish'
& $dotnet publish $agentProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:Version=$Version -o $agentOut
if ($LASTEXITCODE -ne 0) { throw 'Agent publish 실패' }

Write-SswStep 'Viewer self-contained publish'
& $dotnet publish $viewerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:Version=$Version -o $viewerOut
if ($LASTEXITCODE -ne 0) { throw 'Viewer publish 실패' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $publishRoot -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts') -Destination $publishRoot -Recurse

$agentScripts = @(
    'common.ps1', 'install-agent.ps1', 'uninstall-agent.ps1', 'set-switch-credential.ps1',
    'new-pairing-code.ps1', 'diagnose-agent.ps1'
)
$viewerScripts = @('common.ps1', 'install-viewer.ps1', 'uninstall-viewer.ps1', 'pair-viewer.ps1')
foreach ($script in $agentScripts) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$script") -Destination $agentOut
}
foreach ($script in $viewerScripts) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$script") -Destination $viewerOut
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL_KO.md') -Destination $agentOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\INSTALL_KO.md') -Destination $viewerOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASE_NOTES_0.2.0_POC_KO.md') -Destination $agentOut
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASE_NOTES_0.2.0_POC_KO.md') -Destination $viewerOut

$agentZip = Join-Path $releaseRoot "SamsungSwitchWatch-Agent-$Version-win-x64.zip"
$viewerZip = Join-Path $releaseRoot "SamsungSwitchWatch-Viewer-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $agentOut '*') -DestinationPath $agentZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $viewerOut '*') -DestinationPath $viewerZip -CompressionLevel Optimal

$hashFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath $agentZip, $viewerZip
$lines = $hashes | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path) }
[IO.File]::WriteAllLines($hashFile, $lines, (New-Object Text.UTF8Encoding($false)))

& (Join-Path $PSScriptRoot 'test-package-contract.ps1') -ReleaseDirectory $releaseRoot -Version $Version
if ($LASTEXITCODE -ne 0) { throw '릴리스 패키지 계약 검사 실패' }

Write-SswStep "릴리스 완료: $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot | Select-Object Name, Length, LastWriteTime
