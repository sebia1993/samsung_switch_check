param(
    [string]$InstallDirectory,
    [switch]$RemoveSettings,
    [switch]$PerUser,
    [switch]$MachinePhase
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = if ($PerUser) {
        Join-Path $env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Viewer'
    }
    else {
        Join-Path $env:ProgramFiles 'SamsungSwitchWatch\Viewer'
    }
}

$install = [IO.Path]::GetFullPath($InstallDirectory)
if ($MachinePhase -and $PerUser) {
    throw 'VIEWER_UNINSTALL_MODE_INVALID: -MachinePhase와 -PerUser는 함께 사용할 수 없습니다.'
}
if ($PerUser) {
    Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer' -RequireExactProductRoot
}
else {
    Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles `
        -ProductRelativeRoot 'SamsungSwitchWatch\Viewer' -RequireExactProductRoot
}
$settings = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch'
if ($RemoveSettings) { Assert-SswProductPath -Path $settings -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'SamsungSwitchWatch' }

function ConvertTo-SswViewerUninstallLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Enter-SswViewerMachineUninstallLock {
    $name = 'Global\SamsungSwitchWatch.Viewer.Machine.Deployment.v1'
    $mutex = $null
    $acquired = $false
    try {
        $security = New-Object Security.AccessControl.MutexSecurity
        $security.SetAccessRuleProtection($true, $false)
        foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
            $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
            $security.AddAccessRule((New-Object Security.AccessControl.MutexAccessRule(
                $sid,
                [Security.AccessControl.MutexRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow)))
        }
        $createdNew = $false
        $mutex = [Threading.Mutex]::new($false, $name, [ref]$createdNew, $security)
        $acquired = $mutex.WaitOne(0)
        if (-not $acquired) {
            throw 'DEPLOYMENT_ALREADY_RUNNING: Viewer 시스템 설치 또는 제거 작업이 이미 실행 중입니다.'
        }
        return [pscustomobject]@{ Name = $name; Mutex = $mutex }
    }
    catch {
        if ($mutex) {
            if ($acquired) { try { $mutex.ReleaseMutex() } catch { } }
            try { $mutex.Dispose() } catch { }
        }
        throw
    }
}

function Invoke-SswViewerElevatedUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$UninstallerPath,
        [Parameter(Mandatory = $true)][string]$MachineInstallDirectory
    )

    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellPath -PathType Leaf)) {
        throw 'VIEWER_POWERSHELL_NOT_FOUND: Windows PowerShell을 찾지 못했습니다.'
    }
    $scriptLiteral = ConvertTo-SswViewerUninstallLiteral -Value $UninstallerPath
    $installLiteral = ConvertTo-SswViewerUninstallLiteral -Value $MachineInstallDirectory
    $command = "try { & $scriptLiteral -MachinePhase -InstallDirectory $installLiteral; exit 0 } catch { Write-Host (`$_.Exception.Message) -ForegroundColor Red; exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    try {
        $process = Start-Process -FilePath $powerShellPath -Verb RunAs -Wait -PassThru `
            -ArgumentList "-NoLogo -NoProfile -EncodedCommand $encoded" -ErrorAction Stop
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_ELEVATION_NOT_GRANTED: 관리자 승격을 승인하지 않았거나 승격된 Windows PowerShell을 시작하지 못했습니다.',
            $_.Exception)
    }
    if ($process.ExitCode -ne 0) {
        throw "VIEWER_MACHINE_UNINSTALL_FAILED: 관리자 제거 단계가 실패했습니다. 종료 코드: $($process.ExitCode)"
    }
}

function Test-SswOwnedViewerShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$InstallDirectory
    )

    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { return $false }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $target = [string]$shortcut.TargetPath
        if ([string]::IsNullOrWhiteSpace($target)) { return $false }
        $resolvedTarget = [IO.Path]::GetFullPath($target)
    }
    catch {
        return $false
    }

    $machineOrSelectedTarget = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) `
        'SamsungSwitchWatch.Viewer.exe'
    $legacyTarget = Join-Path $env:LOCALAPPDATA `
        'Programs\SamsungSwitchWatch\Viewer\SamsungSwitchWatch.Viewer.exe'
    foreach ($ownedTarget in @($machineOrSelectedTarget, $legacyTarget)) {
        if ($resolvedTarget.Equals(
                [IO.Path]::GetFullPath($ownedTarget),
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Remove-SswOwnedViewerShortcuts {
    param(
        [Parameter(Mandatory = $true)][string[]]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$InstallDirectory
    )

    foreach ($link in $ShortcutPath) {
        if (-not (Test-Path -LiteralPath $link -PathType Leaf)) { continue }
        if (Test-SswOwnedViewerShortcut -ShortcutPath $link `
                -InstallDirectory $InstallDirectory) {
            Remove-Item -LiteralPath $link -Force
        }
        else {
            Write-Warning "VIEWER_SHORTCUT_PRESERVED_UNVERIFIED: 제품 실행 파일을 가리키지 않는 바로 가기를 보존합니다: $link"
        }
    }
}

if (-not $PerUser -and -not $MachinePhase) {
    if (Test-SswAdministrator) {
        throw 'VIEWER_ORIGINAL_USER_PHASE_REQUIRES_UNELEVATED: 기본 제거는 현재 사용자의 바로 가기와 설정 선택을 올바르게 처리하도록 승격되지 않은 창에서 시작해야 합니다.'
    }
    Invoke-SswViewerElevatedUninstall -UninstallerPath $PSCommandPath `
        -MachineInstallDirectory $install
    $currentUserLinks = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk')
    )
    Remove-SswOwnedViewerShortcuts -ShortcutPath $currentUserLinks `
        -InstallDirectory $install
    if ($RemoveSettings -and (Test-Path -LiteralPath $settings)) {
        Remove-Item -LiteralPath $settings -Recurse -Force
        Write-Warning 'Viewer 장비, 자격 증명, 연결, 화면 설정을 제거했으며 복구되지 않습니다.'
    }
    Write-SswStep 'Viewer 시스템 프로그램 및 현재 사용자 바로 가기 제거 완료'
    return
}

if ($MachinePhase) { Assert-SswAdministrator }
$machineRollbackSlot = $null
$deploymentLock = if ($MachinePhase) {
    Enter-SswViewerMachineUninstallLock
}
else {
    Enter-SswDeploymentLock -Product 'Viewer'
}
try {
if ($MachinePhase) {
    $installParent = Split-Path $install -Parent
    $machineRollbackSlot = "$install.__rollback"
    Assert-SswChildPath -Parent $installParent -Child $machineRollbackSlot
    if ((Split-Path $machineRollbackSlot -Parent) -cne $installParent) {
        throw 'VIEWER_ROLLBACK_SLOT_INVALID: Viewer rollback slot은 설치 폴더와 같은 보호된 부모에 있어야 합니다.'
    }
    if (Test-Path -LiteralPath $install) {
        Assert-SswTrustedDirectoryRootOwner -Path $install | Out-Null
    }
    if (Test-Path -LiteralPath $machineRollbackSlot) {
        Assert-SswTrustedDirectoryRootOwner -Path $machineRollbackSlot | Out-Null
    }
}
$links = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk')
)
$transactionId = [Guid]::NewGuid().ToString('N')
$journalPath = if ($MachinePhase) {
    Join-Path $env:ProgramData 'SamsungSwitchWatch-Viewer-Operations\viewer-uninstall.json'
}
else {
    Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch-Operations\viewer-uninstall.json'
}
Write-SswOperationJournal -Path $journalPath -Operation 'viewer-uninstall' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running'

$uninstallState = [pscustomobject]@{
    ViewerStopped = $false
    ActiveProgramRemoved = $false
}
$errors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'stop-viewer'; Action = {
        $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
        if ($viewerProcesses.Count -gt 0) {
            $viewerProcesses | Stop-Process
            foreach ($process in $viewerProcesses) { try { $process.WaitForExit(5000) | Out-Null } catch { } }
            Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue | Stop-Process -Force
        }
        if (Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue) {
            throw 'VIEWER_UNINSTALL_PROCESS_STOP_FAILED: Viewer 프로세스가 남아 있어 프로그램과 rollback slot을 보존합니다.'
        }
        $uninstallState.ViewerStopped = $true
    } },
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (-not $uninstallState.ViewerStopped) {
            throw 'VIEWER_UNINSTALL_PROGRAM_PRESERVED: Viewer 종료가 확인되지 않아 프로그램을 보존합니다.'
        }
        if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
        if (Test-Path -LiteralPath $install) {
            throw 'VIEWER_UNINSTALL_PROGRAM_REMOVE_FAILED: Viewer 프로그램 폴더가 남아 있어 rollback slot을 보존합니다.'
        }
        $uninstallState.ActiveProgramRemoved = $true
    } },
    [pscustomobject]@{ Name = 'remove-machine-rollback-slot'; Action = {
        if ($MachinePhase -and (Test-Path -LiteralPath $machineRollbackSlot)) {
            if (-not $uninstallState.ActiveProgramRemoved) {
                throw 'VIEWER_UNINSTALL_ROLLBACK_PRESERVED: 활성 Viewer 프로그램 제거가 확인되지 않아 rollback slot을 보존합니다.'
            }
            Remove-Item -LiteralPath $machineRollbackSlot -Recurse -Force
        }
    } },
    [pscustomobject]@{ Name = 'remove-shortcuts'; Action = {
        if (-not $MachinePhase) {
            if (-not $uninstallState.ActiveProgramRemoved) {
                throw 'VIEWER_UNINSTALL_SHORTCUTS_PRESERVED: Viewer 프로그램 제거가 확인되지 않아 바로 가기를 보존합니다.'
            }
            Remove-SswOwnedViewerShortcuts -ShortcutPath $links `
                -InstallDirectory $install
        }
    } },
    [pscustomobject]@{ Name = 'remove-settings'; Action = {
        if (-not $MachinePhase -and $RemoveSettings -and (Test-Path -LiteralPath $settings)) {
            if (-not $uninstallState.ActiveProgramRemoved) {
                throw 'VIEWER_UNINSTALL_SETTINGS_PRESERVED: Viewer 프로그램 제거가 확인되지 않아 사용자 설정을 보존합니다.'
            }
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
}
finally {
    Exit-SswDeploymentLock -Lock $deploymentLock
}
