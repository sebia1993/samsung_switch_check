using System.Security.AccessControl;
using System.Security.Principal;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class DeploymentSecurityTests
{
    [Fact]
    public void WriteAllTextAtomic_CreatesAndReplacesUtf8WithoutLeavingTemporaryFiles()
    {
        using var folder = new TemporaryFolder();
        var fileSystem = new PhysicalSetupFileSystem();
        var path = Path.Combine(folder.Path, "journal.json");

        fileSystem.WriteAllTextAtomic(path, "첫 번째");
        fileSystem.WriteAllTextAtomic(path, "두 번째");

        Assert.Equal("두 번째", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(folder.Path, ".journal.json.*.tmp*"));
    }

    [Fact]
    public void IsReparsePoint_DetectsJunctionOrSymbolicLinkAttribute()
    {
        Assert.True(PhysicalSetupFileSystem.IsReparsePoint(
            FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(PhysicalSetupFileSystem.IsReparsePoint(FileAttributes.Directory));
    }

    [Fact]
    public void FreshDataDirectoryAdoption_AcceptsOnlyExistingEmptyDirectory()
    {
        using var folder = new TemporaryFolder();

        Assert.True(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));

        var filePath = Path.Combine(folder.Path, "unexpected.txt");
        File.WriteAllText(filePath, "unexpected");
        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));

        File.Delete(filePath);
        Directory.CreateDirectory(Path.Combine(folder.Path, "unexpected-child"));
        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(folder.Path));
    }

    [Fact]
    public void FreshDataDirectoryAdoption_RejectsMissingDirectory()
    {
        using var folder = new TemporaryFolder();
        var missing = Path.Combine(folder.Path, "missing");

        Assert.False(PhysicalSetupFileSystem.IsEmptyNonReparseDirectory(missing));
    }

    [Fact]
    public void IsAllowedOwner_RequiresExplicitTrustedOwner()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        Assert.True(PhysicalSetupFileSystem.IsAllowedOwner(
            administrators,
            [administrators]));
        Assert.False(PhysicalSetupFileSystem.IsAllowedOwner(
            user,
            [administrators]));
    }

    [Fact]
    public void ServiceDaclAudit_RejectsStopGrantToOrdinaryUsers()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 1);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                users,
                false,
                null));

        Assert.True(WindowsServiceManager.GrantsStopToUnexpectedPrincipal(
            dacl,
            administrators,
            system,
            service));
    }

    [Fact]
    public void ServiceDaclAudit_AcceptsStopGrantOnlyForAdministratorsAndSystem()
    {
        var administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 2);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                administrators,
                false,
                null));
        dacl.InsertAce(
            1,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                0x20,
                system,
                false,
                null));

        Assert.False(WindowsServiceManager.GrantsStopToUnexpectedPrincipal(
            dacl,
            administrators,
            system,
            service));
    }

    [Fact]
    public void HasBroadWriteAccess_RejectsUsersModifyGrant()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            AccessControlType.Allow));

        Assert.True(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void HasBroadWriteAccess_AllowsUsersReadExecuteOnly()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));

        Assert.False(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void HasUntrustedWriteAccess_RejectsDirectStandardUserWriteGrant()
    {
        var security = new FileSecurity();
        var standardUser = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        security.AddAccessRule(new FileSystemAccessRule(
            standardUser,
            FileSystemRights.WriteData,
            AccessControlType.Allow));

        Assert.True(PhysicalSetupFileSystem.HasUntrustedWriteAccess(
            security,
            [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]));
    }

    [Fact]
    public void LegacyLocalServiceOwnership_IsAllowedOnlyWhenServiceIsStopped()
    {
        Assert.True(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT AUTHORITY\LocalService", running: false)));
        Assert.False(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT AUTHORITY\LocalService", running: true)));
        Assert.False(ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(
            Service(@"NT SERVICE\SamsungSwitchWatchAgent", running: false)));
    }

    [Fact]
    public void ReceiptFile_RemainsAdministratorsOnlyDuringDataAclMigration()
    {
        const string root = @"C:\ProgramData\SamsungSwitchWatch";

        Assert.False(PhysicalSetupFileSystem.ShouldGrantServiceAccess(
            root,
            Path.Combine(root, "install-receipt.json"),
            DirectoryAccessKind.AgentDataModify));
        Assert.True(PhysicalSetupFileSystem.ShouldGrantServiceAccess(
            root,
            Path.Combine(root, "agent-identity.json"),
            DirectoryAccessKind.AgentDataModify));
    }

    [Fact]
    public void CreateServiceSid_UsesWindowsServiceSidDerivation()
    {
        Assert.Equal(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            PhysicalSetupFileSystem.CreateServiceSid("TrustedInstaller").Value);
    }

    [Fact]
    public void VanishedEntryPolicy_IgnoresOnlyMissingNonRootChildren()
    {
        using var folder = new TemporaryFolder();
        var root = folder.Path;
        var child = Path.Combine(root, "gone.tmp");

        Assert.True(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            child,
            new FileNotFoundException()));
        Assert.False(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            root,
            new DirectoryNotFoundException()));
        Assert.False(PhysicalSetupFileSystem.IsVanishedNonRootEntry(
            root,
            child,
            new IOException()));
    }

    [Fact]
    public void TransientIoRetry_RetriesOnceAndReturnsSecondResult()
    {
        var attempts = 0;

        var result = PhysicalSetupFileSystem.RetryTransientIoOnce(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new IOException("transient");
            }

            return "ready";
        });

        Assert.Equal("ready", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TransientIoRetry_PersistentIoStopsAfterTwoAttempts()
    {
        var attempts = 0;

        Assert.Throws<IOException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new IOException("persistent");
            }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TransientIoRetry_DoesNotRetryAccessDenied()
    {
        var attempts = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PhysicalSetupFileSystem.RetryTransientIoOnce<int>(() =>
            {
                attempts++;
                throw new UnauthorizedAccessException("denied");
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void InspectionRootCheck_RejectsMissingOrNonDirectoryRoot()
    {
        using var folder = new TemporaryFolder();
        var missing = Path.Combine(folder.Path, "missing");
        var file = Path.Combine(folder.Path, "not-a-directory.txt");
        File.WriteAllText(file, "fixture");

        var missingFailure = Assert.Throws<SetupException>(() =>
            PhysicalSetupFileSystem.EnsureInspectionRootAvailable(missing));
        var fileFailure = Assert.Throws<SetupException>(() =>
            PhysicalSetupFileSystem.EnsureInspectionRootAvailable(file));

        Assert.Equal(SetupErrorCodes.PathNotWritable, missingFailure.Code);
        Assert.Equal(SetupErrorCodes.PathNotWritable, fileFailure.Code);
    }

    private static ServiceSnapshot Service(string accountName, bool running) =>
        new(
            true,
            running,
            "\"agent.exe\" --service",
            2,
            accountName,
            "Agent",
            "Agent",
            1,
            ServiceRecoverySnapshot.Empty,
            [],
            running ? 1234 : 0);
}
