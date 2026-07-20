Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SswDotNet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            return $candidate
        }
    }

    throw '.NET 10 SDK를 찾지 못했습니다. https://dotnet.microsoft.com/download/dotnet/10.0 에서 x64 SDK를 설치하세요.'
}

function Assert-SswAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '관리자 권한 PowerShell에서 실행해야 합니다.'
    }
}

function Test-SswAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-SswChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        -not $childFull.StartsWith(($parentFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "안전 범위를 벗어난 경로입니다: $childFull"
    }
    Assert-SswNoReparsePoint -Parent $parentFull -Child $childFull
}

function Assert-SswNoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase)) { return }
    if (-not $childFull.StartsWith(($parentFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "재분석 지점 검사 범위를 벗어난 경로입니다: $childFull"
    }
    $relative = $childFull.Substring($parentFull.Length + 1)
    $current = $parentFull
    foreach ($segment in $relative.Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "junction 또는 symlink 경로는 자동 변경하지 않습니다: $current"
            }
        }
    }
}

function Assert-SswProductPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BaseRoot,
        [Parameter(Mandatory = $true)][string]$ProductRelativeRoot
    )

    $baseFull = [IO.Path]::GetFullPath($BaseRoot).TrimEnd('\')
    $productFull = [IO.Path]::GetFullPath((Join-Path $baseFull $ProductRelativeRoot)).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $targetFull.Equals($productFull, [StringComparison]::OrdinalIgnoreCase) -and
        -not $targetFull.StartsWith(($productFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "SamsungSwitchWatch 전용 안전 경로 밖입니다: $targetFull"
    }
    Assert-SswNoReparsePoint -Parent $baseFull -Child $targetFull
}

function Write-SswStep {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[Samsung Switch Watch] $Message" -ForegroundColor Cyan
}

function Get-SswAgentServiceName {
    return 'SamsungSwitchWatchAgent'
}

function Wait-SswServiceDeleted {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "서비스 제거가 제한 시간 안에 완료되지 않았습니다: $Name"
}

function Get-SswServiceSid {
    param([Parameter(Mandatory = $true)][string]$Name)

    $output = & sc.exe showsid $Name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "서비스 SID 조회에 실패했습니다: $Name" }
    $match = [regex]::Match(($output -join "`n"), 'S-1-5-80-(?:\d+-){4}\d+')
    if (-not $match.Success) { throw "서비스 SID를 해석하지 못했습니다: $Name" }
    return $match.Value
}

function Set-SswRestrictedDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSid,
        [Parameter(Mandatory = $true)]
        [ValidateSet('ReadAndExecute', 'Modify')][string]$ServiceRights
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "ACL을 설정할 폴더가 없습니다: $resolved"
    }

    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $agentSid = New-Object Security.Principal.SecurityIdentifier($ServiceSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $descendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    $reparsePoint = $descendants | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | Select-Object -First 1
    if ($reparsePoint) {
        throw "junction 또는 symlink가 포함된 폴더 트리는 자동 변경하지 않습니다: $($reparsePoint.FullName)"
    }

    $acl = Get-Acl -LiteralPath $resolved
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($acl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
        $acl.PurgeAccessRules($identity)
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $agentSid, [Security.AccessControl.FileSystemRights]::$ServiceRights, $inheritance, $propagation, $allow)))
    Set-Acl -LiteralPath $resolved -AclObject $acl

    foreach ($item in $descendants | Sort-Object { $_.FullName.Length }) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $childAcl.SetAccessRuleProtection($true, $false)
        foreach ($identity in @($childAcl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
            $childAcl.PurgeAccessRules($identity)
        }
        $childAcl.SetAccessRuleProtection($false, $false)
        Set-Acl -LiteralPath $item.FullName -AclObject $childAcl
    }

    $verified = Get-Acl -LiteralPath $resolved
    $allowedSids = @($systemSid.Value, $administratorsSid.Value, $agentSid.Value)
    $unexpected = @($verified.Access | Where-Object {
        $_.IsInherited -or $_.AccessControlType -ne $allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
    })
    if ($unexpected.Count -gt 0) {
        throw "허용되지 않은 폴더 권한이 남아 있습니다: $resolved"
    }
    foreach ($requiredSid in $allowedSids) {
        if (-not ($verified.Access | Where-Object {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $requiredSid
        })) {
            throw "필수 폴더 권한을 확인하지 못했습니다: $requiredSid"
        }
    }
    foreach ($item in $descendants) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $invalidChildRule = $childAcl.Access | Where-Object {
            -not $_.IsInherited -or $_.AccessControlType -ne $allow -or
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
        } | Select-Object -First 1
        if ($invalidChildRule) {
            throw "하위 항목에 허용되지 않은 명시적 권한이 남아 있습니다: $($item.FullName)"
        }
    }
}

function Get-SswDirectoryAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { return }
    $items = @((Get-Item -LiteralPath $resolved -Force)) +
        @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "ACL 백업에서 junction 또는 symlink를 허용하지 않습니다: $($item.FullName)"
        }
        [pscustomobject]@{
            Path = $item.FullName
            Sddl = (Get-Acl -LiteralPath $item.FullName).Sddl
            IsContainer = [bool]$item.PSIsContainer
        }
    }
}

function Restore-SswDirectoryAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot
    )

    foreach ($entry in $Snapshot | Sort-Object { $_.Path.Length }) {
        if (-not (Test-Path -LiteralPath $entry.Path)) { continue }
        $acl = if ($entry.IsContainer) {
            New-Object Security.AccessControl.DirectorySecurity
        }
        else {
            New-Object Security.AccessControl.FileSecurity
        }
        $acl.SetSecurityDescriptorSddlForm([string]$entry.Sddl)
        Set-Acl -LiteralPath $entry.Path -AclObject $acl
    }
}

function Test-SswTcpPortAvailable {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [string]$Address = '0.0.0.0'
    )

    $listener = $null
    try {
        $ipAddress = [Net.IPAddress]::Parse($Address)
        $listener = New-Object Net.Sockets.TcpListener($ipAddress, $Port)
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($listener) { $listener.Stop() }
    }
}

function Invoke-SswLocalHealthProbe {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30
    )

    Add-Type -AssemblyName System.Net.Http
    if (-not ('SswLocalHealthHttpHandler' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Net.Http;
public sealed class SswLocalHealthHttpHandler : HttpClientHandler
{
    public SswLocalHealthHttpHandler()
    {
        ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => true;
    }
}
'@ -ReferencedAssemblies 'System.Net.Http.dll'
    }

    $handler = New-Object SswLocalHealthHttpHandler
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(3)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastStatus = 'AGENT_HTTPS_UNREACHABLE'
    try {
        do {
            $response = $null
            try {
                $response = $client.GetAsync("https://127.0.0.1:$Port/health/ready").GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) { return 'READY' }
                try {
                    $readinessBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    $lastStatus = if ($readinessBody.code) { [string]$readinessBody.code } else { "AGENT_NOT_READY_$([int]$response.StatusCode)" }
                }
                catch { $lastStatus = "AGENT_NOT_READY_$([int]$response.StatusCode)" }
            }
            catch {
                # 서비스 시작 직후의 연결 거부는 제한 시간 동안 재시도합니다.
            }
            finally { if ($response) { $response.Dispose() } }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    throw "Agent readiness 확인이 ${TimeoutSeconds}초 안에 성공하지 못했습니다. 마지막 상태: $lastStatus"
}
