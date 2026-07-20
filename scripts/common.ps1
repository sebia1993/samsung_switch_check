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
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "junction 또는 symlink 폴더는 ACL을 자동 변경하지 않습니다: $resolved"
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

function Set-SswInstallerBackupAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "백업 폴더가 없습니다: $resolved" }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "junction 또는 symlink 백업 폴더는 사용하지 않습니다: $resolved"
    }
    $acl = Get-Acl -LiteralPath $resolved
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($acl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
        $acl.PurgeAccessRules($identity)
    }
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    }
    Set-Acl -LiteralPath $resolved -AclObject $acl
}

function Grant-SswCertificatePrivateKeyRead {
    param(
        [Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)][string]$ServiceSid
    )

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $rsa) { throw 'HTTPS 인증서의 RSA 개인 키를 찾지 못했습니다.' }
    try {
        $keyPath = $null
        if ($rsa -is [Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
        }
        if ([string]::IsNullOrWhiteSpace($keyPath) -or -not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw 'HTTPS 인증서 개인 키 파일 위치를 확인하지 못했습니다.'
        }
        $acl = Get-Acl -LiteralPath $keyPath
        $identity = New-Object Security.Principal.SecurityIdentifier($ServiceSid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, [Security.AccessControl.FileSystemRights]::Read,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $keyPath -AclObject $acl
        $verified = Get-Acl -LiteralPath $keyPath
        if (-not ($verified.Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $ServiceSid -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne 0
        })) { throw '서비스 SID의 인증서 개인 키 읽기 권한을 확인하지 못했습니다.' }
    }
    finally { $rsa.Dispose() }
}

function Write-SswOperationJournal {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$Version,
        [string[]]$ErrorCodes = @()
    )

    $journalPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path $journalPath -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $payload = [ordered]@{
        formatVersion = 1
        product = 'SamsungSwitchWatch'
        operation = $Operation
        transactionId = $TransactionId
        stage = $Stage
        status = $Status
        version = $Version
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        errorCodes = @($ErrorCodes)
    } | ConvertTo-Json -Depth 5
    $temporary = "$journalPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$journalPath.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [IO.File]::WriteAllText($temporary, $payload, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            [IO.File]::Replace($temporary, $journalPath, $replaceBackup, $true)
            if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $journalPath
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
    }
}

function Invoke-SswBestEffortPlan {
    param(
        [Parameter(Mandatory = $true)][object[]]$Plan
    )

    $errors = New-Object Collections.Generic.List[string]
    foreach ($step in $Plan) {
        try {
            & $step.Action
        }
        catch {
            $code = "{0}_FAILED" -f ([string]$step.Name).ToUpperInvariant().Replace('-', '_')
            $errors.Add($code)
            Write-Warning ("복구 단계 실패 [{0}]: {1}" -f $step.Name, $_.Exception.Message)
        }
    }
    return @($errors)
}

function Get-SswAgentFirewallSnapshot {
    param([string]$DisplayName = 'Samsung Switch Watch Agent HTTPS')

    $rules = @(Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) { return $null }
    if ($rules.Count -ne 1) { throw "Agent 방화벽 규칙이 중복되어 있습니다: $DisplayName" }
    $rule = $rules[0]
    $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule
    $address = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule
    return [pscustomobject]@{
        Name = [string]$rule.Name
        DisplayName = [string]$rule.DisplayName
        Group = [string]$rule.Group
        Description = [string]$rule.Description
        Enabled = [string]$rule.Enabled
        Direction = [string]$rule.Direction
        Action = [string]$rule.Action
        Profile = [string]$rule.Profile
        Protocol = [string]$port.Protocol
        LocalPort = [string]$port.LocalPort
        RemoteAddress = @($address.RemoteAddress | ForEach-Object { [string]$_ })
    }
}

function Test-SswOwnedAgentFirewallRule {
    param([Parameter(Mandatory = $true)][object]$Snapshot)
    return $Snapshot.Name -eq 'SamsungSwitchWatchAgent-Https' -and
        $Snapshot.Group -eq 'Samsung Switch Watch' -and
        $Snapshot.Description -eq 'Owned by SamsungSwitchWatchAgent installer v1'
}

function Test-SswAgentFirewallRuleExact {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string]$RemoteAddress
    )

    return (Test-SswOwnedAgentFirewallRule -Snapshot $Snapshot) -and
        $Snapshot.Enabled -eq 'True' -and $Snapshot.Direction -eq 'Inbound' -and
        $Snapshot.Action -eq 'Allow' -and $Snapshot.Protocol -in @('TCP', '6') -and
        $Snapshot.LocalPort -eq [string]$Port -and $Snapshot.RemoteAddress.Count -eq 1 -and
        $Snapshot.RemoteAddress[0] -eq $RemoteAddress -and
        $Snapshot.Profile -notmatch 'Public'
}

function New-SswAgentFirewallRule {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string]$RemoteAddress
    )

    New-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Https' `
        -DisplayName 'Samsung Switch Watch Agent HTTPS' -Group 'Samsung Switch Watch' `
        -Description 'Owned by SamsungSwitchWatchAgent installer v1' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -RemoteAddress $RemoteAddress -Profile Domain,Private | Out-Null
}

function Restore-SswAgentFirewallSnapshot {
    param([AllowNull()][object]$Snapshot)

    Get-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Https' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($null -eq $Snapshot) { return }
    $parameters = @{
        Name = $Snapshot.Name
        DisplayName = $Snapshot.DisplayName
        Enabled = $Snapshot.Enabled
        Direction = $Snapshot.Direction
        Action = $Snapshot.Action
        Protocol = $Snapshot.Protocol
        LocalPort = $Snapshot.LocalPort
        RemoteAddress = @($Snapshot.RemoteAddress)
        Profile = $Snapshot.Profile
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Snapshot.Group)) {
        $parameters.Group = [string]$Snapshot.Group
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Snapshot.Description)) {
        $parameters.Description = [string]$Snapshot.Description
    }
    New-NetFirewallRule @parameters | Out-Null
}

function Remove-SswOwnedAgentFirewallRule {
    param([switch]$AllowMissing)

    $snapshot = Get-SswAgentFirewallSnapshot
    if ($null -eq $snapshot) {
        if ($AllowMissing) { return }
        throw 'Agent 방화벽 규칙을 찾지 못했습니다.'
    }
    if (-not (Test-SswOwnedAgentFirewallRule -Snapshot $snapshot)) {
        throw '소유권 표식이 없는 방화벽 규칙은 자동 제거하지 않습니다.'
    }
    Get-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Https' -ErrorAction Stop | Remove-NetFirewallRule
}
