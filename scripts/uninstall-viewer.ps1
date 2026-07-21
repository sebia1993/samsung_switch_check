param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$RemoveSettings
)

. (Join-Path $PSScriptRoot 'common.ps1')

$install = [IO.Path]::GetFullPath($InstallDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer'
$settings = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch'
if ($RemoveSettings) { Assert-SswProductPath -Path $settings -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'SamsungSwitchWatch' }
$links = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk')
)
$transactionId = [Guid]::NewGuid().ToString('N')
$journalPath = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch-Operations\viewer-uninstall.json'
Write-SswOperationJournal -Path $journalPath -Operation 'viewer-uninstall' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running'

$errors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'stop-viewer'; Action = {
        $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
        if ($viewerProcesses.Count -gt 0) {
            $viewerProcesses | Stop-Process
            foreach ($process in $viewerProcesses) { try { $process.WaitForExit(5000) | Out-Null } catch { } }
            Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue | Stop-Process -Force
        }
    } },
    [pscustomobject]@{ Name = 'remove-shortcuts'; Action = {
        foreach ($link in $links) { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
    } },
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
    } },
    [pscustomobject]@{ Name = 'remove-settings'; Action = {
        if ($RemoveSettings -and (Test-Path -LiteralPath $settings)) {
            Remove-Item -LiteralPath $settings -Recurse -Force
            Write-Warning 'Viewer 연결과 화면 설정을 제거했으며 복구되지 않습니다.'
        }
    } }
))

$status = if ($errors.Count -eq 0) { 'succeeded' } else { 'failed' }
Write-SswOperationJournal -Path $journalPath -Operation 'viewer-uninstall' -TransactionId $transactionId `
    -Stage 'completed' -Status $status -ErrorCodes $errors
if ($errors.Count -gt 0) { throw "일부 Viewer 제거 단계가 실패했습니다: $($errors -join ', ')" }
Write-SswStep 'Viewer 제거 완료'
