param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'SamsungSwitchWatch.sln'
$dotnet = Get-SswDotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-SswStep '패키지 복원'
& $dotnet restore $solution --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'restore 실패' }

Write-SswStep '솔루션 빌드'
& $dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'build 실패' }

Write-SswStep '테스트 실행'
& $dotnet test $solution -c $Configuration --no-build --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'test 실패' }

Write-SswStep 'C# 서식 검사'
& $dotnet format $solution --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet format 검사 실패' }

Write-SswStep 'PowerShell 5.1 구문 검사'
$parseFailures = @()
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts') -Filter '*.ps1' | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors) { $parseFailures += $parseErrors }
}
if ($parseFailures.Count -gt 0) {
    $parseFailures | ForEach-Object { Write-Error ("{0}: {1}" -f $_.Extent.File, $_.Message) }
    throw 'PowerShell 구문 검사 실패'
}

Write-SswStep '배포 도우미 계약 검사'
& (Join-Path $PSScriptRoot 'test-deployment-helpers.ps1')

Write-SswStep 'GitHub 릴리스 워크플로 계약 검사'
& (Join-Path $PSScriptRoot 'test-release-workflow-contract.ps1')

Write-SswStep 'NuGet 취약 패키지 검사'
$vulnerabilityOutput = & $dotnet list $solution package --vulnerable --include-transitive 2>&1
if ($LASTEXITCODE -ne 0) {
    $vulnerabilityOutput | ForEach-Object { Write-Host $_ }
    throw 'NuGet 취약 패키지 검사 실패'
}
if ($vulnerabilityOutput -match '(?im)^\s*>\s+') {
    $vulnerabilityOutput | ForEach-Object { Write-Host $_ }
    throw '취약한 NuGet 패키지가 발견되었습니다.'
}

if (Get-Command git -ErrorAction SilentlyContinue) {
    Write-SswStep 'Git whitespace 검사'
    & git -C $repoRoot diff --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check 실패' }
}

Write-SswStep '검증 완료'
