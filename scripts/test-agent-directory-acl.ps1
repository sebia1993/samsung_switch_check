param(
    [switch]$RequireElevatedAclFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-DirectoryAclTest {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Assert-DirectoryTrustFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $failure = $null
    try { & $Action }
    catch { $failure = $_.Exception.Message }
    Assert-DirectoryAclTest -Condition (
        [string]$failure -like 'AGENT_DIRECTORY_TRUST_INVALID:*'
    ) -Message "Expected AGENT_DIRECTORY_TRUST_INVALID but received: $failure"
}

function Enable-TestRestorePrivilege {
    if (-not ('SswAclFixturePrivilege' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SswAclFixturePrivilege
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x08;
    private const uint SePrivilegeEnabled = 0x02;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(
        string systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    public static void EnableRestorePrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            Luid luid;
            if (!LookupPrivilegeValue(null, "SeRestorePrivilege", out luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            TokenPrivileges privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            if (!AdjustTokenPrivileges(
                    token,
                    false,
                    ref privileges,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
'@
    }

    [SswAclFixturePrivilege]::EnableRestorePrivilege()
}

function Set-TestOwner {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Sid
    )

    Enable-TestRestorePrivilege
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier($Sid)))
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-DirectoryAclTest -Condition (
        (Get-SswAclOwnerSid -Acl (Get-Acl -LiteralPath $Path)) -eq $Sid
    ) -Message "Fixture owner assignment did not persist: $Path"
}

function Assert-SecuredTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSid,
        [Parameter(Mandatory = $true)]
        [Security.AccessControl.FileSystemRights]$ServiceRights
    )

    $administratorsSid = 'S-1-5-32-544'
    # Windows canonicalizes allow ACEs for these practical rights by adding
    # Synchronize. Compare against that persisted representation so the
    # elevated fixture verifies exact effective rights without becoming
    # runner-dependent.
    $canonicalServiceRights = [Security.AccessControl.FileSystemRights](
        [int]$ServiceRights -bor
        [int][Security.AccessControl.FileSystemRights]::Synchronize)
    $expectedRights = @{
        'S-1-5-18' = [Security.AccessControl.FileSystemRights]::FullControl
        $administratorsSid = [Security.AccessControl.FileSystemRights]::FullControl
        $ServiceSid = $canonicalServiceRights
    }
    $allowedSids = @($expectedRights.Keys)
    $rootAcl = Get-Acl -LiteralPath $Path
    Assert-DirectoryAclTest -Condition (
        (Get-SswAclOwnerSid -Acl $rootAcl) -eq $administratorsSid
    ) -Message 'Restricted root owner must be built-in Administrators.'
    Assert-DirectoryAclTest -Condition $rootAcl.AreAccessRulesProtected `
        -Message 'Restricted root must disable inherited access rules.'
    $rootRules = @(Get-SswFileSystemAccessRulesBySid -Acl $rootAcl)
    Assert-DirectoryAclTest -Condition (
        $rootRules.Count -eq $expectedRights.Count -and
        @($rootRules | Where-Object {
            $_.IsInherited -or
            $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $_.IdentityReference.Value -notin $allowedSids -or
            $_.InheritanceFlags -ne
                [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' -or
            $_.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None
        }).Count -eq 0
    ) -Message 'Restricted root contains an unexpected access rule.'
    foreach ($requiredSid in $allowedSids) {
        Assert-DirectoryAclTest -Condition (
            @($rootRules | Where-Object {
                $_.IdentityReference.Value -eq $requiredSid -and
                [int]$_.FileSystemRights -eq [int]$expectedRights[$requiredSid]
            }).Count -eq 1
        ) -Message "Restricted root has missing or incorrect rights for SID: $requiredSid"
    }

    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Recurse -Force)) {
        Assert-DirectoryAclTest -Condition (
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0
        ) -Message "Restricted tree contains a reparse point: $($item.FullName)"
        $childAcl = Get-Acl -LiteralPath $item.FullName
        Assert-DirectoryAclTest -Condition (
            (Get-SswAclOwnerSid -Acl $childAcl) -eq $administratorsSid
        ) -Message "Restricted child owner was not normalized: $($item.FullName)"
        Assert-DirectoryAclTest -Condition (
            @(Get-SswFileSystemAccessRulesBySid -Acl $childAcl | Where-Object {
                -not $_.IsInherited -or
                $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $_.IdentityReference.Value -notin $allowedSids
            }).Count -eq 0
        ) -Message "Restricted child contains an unexpected access rule: $($item.FullName)"
        $childRules = @(Get-SswFileSystemAccessRulesBySid -Acl $childAcl)
        foreach ($requiredSid in $allowedSids) {
            $requiredRights = [int]$expectedRights[$requiredSid]
            Assert-DirectoryAclTest -Condition (
                @($childRules | Where-Object {
                    $_.IdentityReference.Value -eq $requiredSid -and
                    (([int]$_.FileSystemRights -band $requiredRights) -eq $requiredRights)
                }).Count -gt 0
            ) -Message "Restricted child is missing inherited rights for SID $requiredSid`: $($item.FullName)"
        }
    }
}

function Get-TestTreeFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    return @(
        Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
            Sort-Object FullName |
            ForEach-Object {
                $relativePath = $_.FullName.Substring(
                    ([IO.Path]::GetFullPath($Path).TrimEnd('\')).Length + 1)
                '{0}|{1}' -f $relativePath,
                    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
    )
}

function Get-TestTreeSddl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $items = @((Get-Item -LiteralPath $Path -Force)) +
        @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    return @(
        $items |
            Sort-Object FullName |
            ForEach-Object {
                '{0}|{1}' -f $_.FullName, (Get-Acl -LiteralPath $_.FullName).Sddl
            }
    )
}

$testId = [Guid]::NewGuid().ToString('N')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "SamsungSwitchWatch Agent ACL 한글 $testId")
$trustedRoot = Join-Path $fixtureRoot 'trusted'
$readOnlyRoot = Join-Path $fixtureRoot 'read-only'
$preclaimedRoot = Join-Path $fixtureRoot 'preclaimed'
$untrustedChildRoot = Join-Path $fixtureRoot 'untrusted-child'
$reparseRoot = Join-Path $fixtureRoot 'reparse'
$reparseTarget = Join-Path $fixtureRoot 'reparse-target'
$receiptRoot = Join-Path $fixtureRoot 'receipt'
$junctionPath = Join-Path $reparseRoot 'outside-link'
$serviceSid = 'S-1-5-80-0-0-0-0-1'
$virtualAccountFixtureName = 'SswVirtualAccount' + $testId.Substring(0, 12)
$virtualAccountFixtureCreated = $false

try {
    Write-SswStep 'Agent restricted directory owner and SID-only contract'
    New-Item -ItemType Directory -Path $trustedRoot -Force | Out-Null
    $currentOwnerSid = Get-SswAclOwnerSid -Acl (Get-Acl -LiteralPath $trustedRoot)
    $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Assert-DirectoryAclTest -Condition (
        $currentOwnerSid -match '^S-1-' -and
        $currentOwnerSid -in @($currentUserSid, 'S-1-5-32-544')
    ) -Message 'ACL owner must be read directly as the current user or built-in Administrators SID.'
    Assert-DirectoryAclTest -Condition (
        -not (Test-SswTrustedAgentDescendantOwnerSid `
            -OwnerSid 'S-1-5-19' -ServiceSid $serviceSid)
    ) -Message 'Shared LocalService must be rejected by default.'
    Assert-DirectoryAclTest -Condition (
        Test-SswTrustedAgentDescendantOwnerSid `
            -OwnerSid 'S-1-5-19' -ServiceSid $serviceSid `
            -AllowLegacyLocalServiceOwner
    ) -Message 'LocalService may be accepted only by the explicit legacy migration gate.'
    Assert-DirectoryAclTest -Condition (
        Test-SswTrustedAgentDescendantOwnerSid `
            -OwnerSid $serviceSid -ServiceSid $serviceSid
    ) -Message 'The exact Agent service SID must be accepted as a child owner.'
    Assert-DirectoryAclTest -Condition (
        -not (Test-SswTrustedAgentDescendantOwnerSid `
            -OwnerSid 'S-1-5-32-545' -ServiceSid $serviceSid)
    ) -Message 'Built-in Users must never be accepted as an Agent child owner.'

    if (Test-SswAdministrator) {
        Write-SswStep 'Agent restricted directory elevated integration'
        $virtualAccount = "NT SERVICE\$virtualAccountFixtureName"
        $virtualAccountFixtureExe = Join-Path $env:ProgramFiles `
            'SamsungSwitchWatch Fixture\fixture.exe'
        $virtualAccountFixturePathName = "`"$virtualAccountFixtureExe`" --fixture"
        $virtualAccountFixtureBinPathForSc = '\"' + $virtualAccountFixtureExe + '\" --fixture'
        & sc.exe create $virtualAccountFixtureName `
            'binPath=' $virtualAccountFixtureBinPathForSc 'start=' 'demand' `
            'obj=' $virtualAccount | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Virtual service account fixture registration failed.'
        }
        $virtualAccountFixtureCreated = $true
        & sc.exe sidtype $virtualAccountFixtureName unrestricted | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Virtual service account fixture SID activation failed.'
        }
        $virtualService = Get-CimInstance Win32_Service `
            -Filter "Name='$virtualAccountFixtureName'" -ErrorAction Stop
        Assert-DirectoryAclTest -Condition (
            [string]$virtualService.StartName -ieq $virtualAccount -and
            [string]$virtualService.PathName -ceq $virtualAccountFixturePathName -and
            (Get-SswServiceSid -Name $virtualAccountFixtureName) -match '^S-1-5-80-'
        ) -Message 'Windows must preserve the quoted service path and accept the passwordless NT SERVICE virtual account contract.'
        $virtualAccountFixtureUpdatedExe = Join-Path $env:ProgramFiles `
            'SamsungSwitchWatch Fixture Updated\fixture.exe'
        $virtualAccountFixtureUpdatedPathName = "`"$virtualAccountFixtureUpdatedExe`" --service"
        $virtualAccountFixtureUpdatedBinPathForSc = '\"' + `
            $virtualAccountFixtureUpdatedExe + '\" --service'
        & sc.exe config $virtualAccountFixtureName `
            'binPath=' $virtualAccountFixtureUpdatedBinPathForSc 'start=' 'auto' `
            'obj=' $virtualAccount | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Virtual service account fixture update failed.'
        }
        $updatedVirtualService = Get-CimInstance Win32_Service `
            -Filter "Name='$virtualAccountFixtureName'" -ErrorAction Stop
        Assert-DirectoryAclTest -Condition (
            [string]$updatedVirtualService.StartName -ieq $virtualAccount -and
            [string]$updatedVirtualService.PathName -ceq $virtualAccountFixtureUpdatedPathName -and
            [string]$updatedVirtualService.StartMode -ceq 'Auto'
        ) -Message 'Windows service update must preserve the quoted path, virtual account, and automatic start mode.'
        & sc.exe config $virtualAccountFixtureName `
            'binPath=' $virtualAccountFixtureBinPathForSc 'start=' 'demand' `
            'obj=' $virtualAccount | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Virtual service account fixture rollback failed.'
        }
        $rolledBackVirtualService = Get-CimInstance Win32_Service `
            -Filter "Name='$virtualAccountFixtureName'" -ErrorAction Stop
        Assert-DirectoryAclTest -Condition (
            [string]$rolledBackVirtualService.StartName -ieq $virtualAccount -and
            [string]$rolledBackVirtualService.PathName -ceq $virtualAccountFixturePathName -and
            [string]$rolledBackVirtualService.StartMode -ceq 'Manual'
        ) -Message 'Windows service rollback must round-trip the quoted path, virtual account, and demand start mode.'
        & sc.exe delete $virtualAccountFixtureName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Virtual service account fixture removal failed.'
        }
        Wait-SswServiceDeleted -Name $virtualAccountFixtureName -TimeoutSeconds 20
        $virtualAccountFixtureCreated = $false

        $nested = Join-Path $trustedRoot 'nested\deeper'
        New-Item -ItemType Directory -Path $nested -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $trustedRoot 'root.keep'),
            'root',
            (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText(
            (Join-Path $nested 'child.keep'),
            'child',
            (New-Object Text.UTF8Encoding($false)))
        $localServiceOwned = Join-Path $trustedRoot 'local-service.keep'
        $serviceOwned = Join-Path $trustedRoot 'service-sid.keep'
        [IO.File]::WriteAllText(
            $localServiceOwned,
            'local-service',
            (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText(
            $serviceOwned,
            'service-sid',
            (New-Object Text.UTF8Encoding($false)))
        Set-TestOwner -Path $localServiceOwned -Sid 'S-1-5-19'
        Set-TestOwner -Path $serviceOwned -Sid $serviceSid
        $trustedFingerprint = @(Get-TestTreeFingerprint -Path $trustedRoot)

        Set-SswRestrictedDirectoryAcl -Path $trustedRoot `
            -ServiceSid $serviceSid -ServiceRights Modify `
            -AllowServiceOwnedDescendants -AllowLegacyLocalServiceOwnedDescendants
        Assert-SecuredTree -Path $trustedRoot -ServiceSid $serviceSid `
            -ServiceRights Modify
        Assert-DirectoryAclTest -Condition (
            [string]::Join("`n", @(Get-TestTreeFingerprint -Path $trustedRoot)) -ceq
            [string]::Join("`n", $trustedFingerprint)
        ) -Message 'Restricted ACL application must not change Agent data contents.'
        $firstAclFingerprint = @(Get-TestTreeSddl -Path $trustedRoot)
        Set-SswRestrictedDirectoryAcl -Path $trustedRoot `
            -ServiceSid $serviceSid -ServiceRights Modify `
            -AllowServiceOwnedDescendants
        Assert-DirectoryAclTest -Condition (
            [string]::Join("`n", @(Get-TestTreeSddl -Path $trustedRoot)) -ceq
            [string]::Join("`n", $firstAclFingerprint)
        ) -Message 'Restricted Agent data ACL application must be idempotent.'

        New-Item -ItemType Directory -Path $readOnlyRoot -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $readOnlyRoot 'agent.exe'),
            'read-only',
            (New-Object Text.UTF8Encoding($false)))
        Set-SswRestrictedDirectoryAcl -Path $readOnlyRoot `
            -ServiceSid $serviceSid -ServiceRights ReadAndExecute
        Assert-SecuredTree -Path $readOnlyRoot -ServiceSid $serviceSid `
            -ServiceRights ReadAndExecute

        New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
        Set-SswRestrictedDirectoryAcl -Path $receiptRoot `
            -ServiceSid $serviceSid -ServiceRights Modify `
            -AllowServiceOwnedDescendants
        $receiptFile = Join-Path $receiptRoot 'install-receipt.json'
        [IO.File]::WriteAllText(
            $receiptFile,
            '{"receiptVersion":3}',
            (New-Object Text.UTF8Encoding($false)))
        Assert-DirectoryAclTest -Condition (
            -not (Test-SswAdministratorsOnlyFileAcl -Path $receiptFile)
        ) -Message 'A service-writable inherited receipt must not be trusted.'
        Set-SswAdministratorsOnlyFileAcl -Path $receiptFile
        Assert-DirectoryAclTest -Condition (
            Test-SswAdministratorsOnlyFileAcl -Path $receiptFile
        ) -Message 'Receipt must be protected by the Administrators-only file ACL.'
        Assert-DirectoryAclTest -Condition (
            (Get-Content -LiteralPath $receiptFile -Raw) -ceq '{"receiptVersion":3}'
        ) -Message 'Receipt ACL protection must not change its contents.'

        New-Item -ItemType Directory -Path $preclaimedRoot -Force | Out-Null
        $preclaimedFile = Join-Path $preclaimedRoot 'preserve.keep'
        [IO.File]::WriteAllText(
            $preclaimedFile,
            'preserve',
            (New-Object Text.UTF8Encoding($false)))
        Set-TestOwner -Path $preclaimedRoot -Sid 'S-1-5-32-545'
        $preclaimedAclBefore = (Get-Acl -LiteralPath $preclaimedRoot).Sddl
        Assert-DirectoryTrustFailure -Action {
            $null = Assert-SswTrustedDirectoryRootOwner -Path $preclaimedRoot
        }
        Assert-DirectoryTrustFailure -Action {
            Set-SswRestrictedDirectoryAcl -Path $preclaimedRoot `
                -ServiceSid $serviceSid -ServiceRights Modify
        }
        Assert-DirectoryAclTest -Condition (
            (Get-Acl -LiteralPath $preclaimedRoot).Sddl -ceq $preclaimedAclBefore
        ) -Message 'Rejected preclaimed root ACL must remain unchanged.'
        Assert-DirectoryAclTest -Condition (
            (Get-Content -LiteralPath $preclaimedFile -Raw) -ceq 'preserve'
        ) -Message 'Rejected preclaimed root contents must remain unchanged.'

        New-Item -ItemType Directory -Path $untrustedChildRoot -Force | Out-Null
        $untrustedChild = Join-Path $untrustedChildRoot 'child.keep'
        [IO.File]::WriteAllText(
            $untrustedChild,
            'preserve-child',
            (New-Object Text.UTF8Encoding($false)))
        Set-TestOwner -Path $untrustedChild -Sid 'S-1-5-32-545'
        $untrustedTreeAclBefore = @(Get-TestTreeSddl -Path $untrustedChildRoot)
        Assert-DirectoryTrustFailure -Action {
            Set-SswRestrictedDirectoryAcl -Path $untrustedChildRoot `
                -ServiceSid $serviceSid -ServiceRights Modify
        }
        Assert-DirectoryAclTest -Condition (
            (Get-Content -LiteralPath $untrustedChild -Raw) -ceq 'preserve-child'
        ) -Message 'Rejected untrusted child contents must remain unchanged.'
        Assert-DirectoryAclTest -Condition (
            [string]::Join("`n", @(Get-TestTreeSddl -Path $untrustedChildRoot)) -ceq
            [string]::Join("`n", $untrustedTreeAclBefore)
        ) -Message 'Static untrusted child rejection must not partially change ACLs.'

        New-Item -ItemType Directory -Path $reparseRoot, $reparseTarget -Force | Out-Null
        $outsideFile = Join-Path $reparseTarget 'outside.keep'
        [IO.File]::WriteAllText(
            $outsideFile,
            'outside-preserved',
            (New-Object Text.UTF8Encoding($false)))
        $outsideAclBefore = (Get-Acl -LiteralPath $outsideFile).Sddl
        $null = New-Item -ItemType Junction -Path $junctionPath -Target $reparseTarget
        $reparseRootAclBefore = (Get-Acl -LiteralPath $reparseRoot).Sddl
        Assert-DirectoryTrustFailure -Action {
            Set-SswRestrictedDirectoryAcl -Path $reparseRoot `
                -ServiceSid $serviceSid -ServiceRights Modify
        }
        Assert-DirectoryAclTest -Condition (
            (Get-Content -LiteralPath $outsideFile -Raw) -ceq 'outside-preserved' -and
            (Get-Acl -LiteralPath $outsideFile).Sddl -ceq $outsideAclBefore -and
            (Get-Acl -LiteralPath $reparseRoot).Sddl -ceq $reparseRootAclBefore
        ) -Message 'Rejected junction target contents and ACL must remain unchanged.'
    }
    elseif ($RequireElevatedAclFixture) {
        throw 'Elevated Agent restricted directory ACL fixture is required but this process is not elevated.'
    }
    else {
        Write-SswStep 'Skipped elevated Agent restricted directory ACL fixture'
    }
}
finally {
    if ($virtualAccountFixtureCreated -or
        (Get-Service -Name $virtualAccountFixtureName -ErrorAction SilentlyContinue)) {
        & sc.exe delete $virtualAccountFixtureName | Out-Null
        try {
            Wait-SswServiceDeleted -Name $virtualAccountFixtureName -TimeoutSeconds 20
        }
        catch {
            Write-Warning "Virtual service account fixture cleanup failed: $virtualAccountFixtureName"
        }
    }
    if (Test-Path -LiteralPath $junctionPath) {
        # Windows PowerShell 5.1 can prompt (and then fail in a non-interactive
        # CI host) when unlinking a non-empty junction without -Recurse.
        # For a directory junction this removes the link, not its target.
        Remove-Item -LiteralPath $junctionPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $fixtureRoot
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Assert-DirectoryAclTest -Condition (-not (Test-Path -LiteralPath $fixtureRoot)) `
    -Message 'Agent restricted directory ACL fixtures must be removed.'
Write-SswStep 'Agent restricted directory ACL tests passed'
