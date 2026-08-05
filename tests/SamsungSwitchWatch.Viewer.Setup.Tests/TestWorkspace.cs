using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-ViewerSetupTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        PackageDirectory = Path.Combine(Root, "package");
        InstallDirectory = Path.Combine(
            Root,
            "user",
            "Programs",
            "SamsungSwitchWatch",
            "Viewer");
        DataDirectory = Path.Combine(Root, "user", "SamsungSwitchWatch");
        OperationsDirectory = Path.Combine(DataDirectory, "Setup");
        Paths = CreatePaths(PackageDirectory);
        FileSystem = new PhysicalViewerSetupFileSystem();
        Process = new FakeProcessManager();
        Shutdown = new FakeShutdownCoordinator();
        Shortcuts = new FakeShortcutManager(FileSystem);
    }

    public string Root { get; }
    public string PackageDirectory { get; }
    public string InstallDirectory { get; }
    public string DataDirectory { get; }
    public string OperationsDirectory { get; }
    public ViewerSetupPaths Paths { get; private set; }
    public PhysicalViewerSetupFileSystem FileSystem { get; }
    public FakeProcessManager Process { get; }
    public FakeShutdownCoordinator Shutdown { get; }
    public FakeShortcutManager Shortcuts { get; }

    public ViewerSetupPaths CreatePaths(string packageDirectory) =>
        new(
            packageDirectory,
            InstallDirectory,
            DataDirectory,
            OperationsDirectory,
            Path.Combine(Root, "desktop", ViewerSetupConstants.ShortcutFileName),
            Path.Combine(Root, "start-menu", ViewerSetupConstants.ShortcutFileName),
            Path.Combine(Root, "startup", ViewerSetupConstants.ShortcutFileName));

    public ViewerDeploymentOrchestrator CreateOrchestrator(
        ViewerSetupPaths? paths = null) =>
        new(
            new ViewerPackageValidator(FileSystem),
            FileSystem,
            Process,
            Shutdown,
            Shortcuts,
            new NoOpDeploymentLock(),
            paths ?? Paths);

    public void CreatePackage(
        string directory,
        string version = "0.11.4-poc",
        string viewerContents = "viewer-new",
        string setupContents = "setup-new")
    {
        Directory.CreateDirectory(directory);
        Write(Path.Combine(directory, ViewerSetupConstants.ViewerExecutableName), viewerContents);
        Write(Path.Combine(directory, ViewerSetupConstants.SetupExecutableName), setupContents);
        Write(Path.Combine(directory, "viewer-companion.dll"), "companion-" + version);

        var names = new[]
        {
            ViewerSetupConstants.SetupExecutableName,
            ViewerSetupConstants.ViewerExecutableName,
            "viewer-companion.dll"
        };
        var files = names.Select(name =>
        {
            var path = Path.Combine(directory, name);
            return new
            {
                name,
                size = new FileInfo(path).Length,
                sha256 = Hash(path)
            };
        }).ToArray();
        var setupPath = Path.Combine(directory, ViewerSetupConstants.SetupExecutableName);
        var manifest = new
        {
            manifestVersion = 1,
            product = ViewerSetupConstants.ProductName,
            packageKind = "Viewer",
            version,
            sourceCommit = new string('a', 40),
            executable = new
            {
                name = ViewerSetupConstants.SetupExecutableName,
                sha256 = Hash(setupPath),
                productVersion = version + "+" + new string('a', 40)
            },
            files
        };
        File.WriteAllText(
            Path.Combine(directory, ViewerSetupConstants.ManifestFileName),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(false));
    }

    public void CreatePackage() => CreatePackage(PackageDirectory);

    public void CreateInstalledProduct(
        string version = "0.11.3-poc",
        string viewerContents = "viewer-old") =>
        CreatePackage(
            InstallDirectory,
            version,
            viewerContents,
            "setup-old");

    public void CreateLegacyInstalledProduct(
        string version = "0.11.3-poc",
        string viewerContents = "viewer-old")
    {
        Directory.CreateDirectory(InstallDirectory);
        Write(
            Path.Combine(InstallDirectory, ViewerSetupConstants.ViewerExecutableName),
            viewerContents);
        Write(
            Path.Combine(InstallDirectory, "viewer-companion.dll"),
            "companion-" + version);

        var names = new[]
        {
            ViewerSetupConstants.ViewerExecutableName,
            "viewer-companion.dll"
        };
        var files = names.Select(name =>
        {
            var path = Path.Combine(InstallDirectory, name);
            return new
            {
                name,
                size = new FileInfo(path).Length,
                sha256 = Hash(path)
            };
        }).ToArray();
        var viewerPath = Path.Combine(
            InstallDirectory,
            ViewerSetupConstants.ViewerExecutableName);
        var manifest = new
        {
            manifestVersion = 1,
            product = ViewerSetupConstants.ProductName,
            packageKind = "Viewer",
            version,
            sourceCommit = new string('b', 40),
            executable = new
            {
                name = ViewerSetupConstants.ViewerExecutableName,
                sha256 = Hash(viewerPath),
                productVersion = version + "+" + new string('b', 40)
            },
            files
        };
        File.WriteAllText(
            Path.Combine(InstallDirectory, ViewerSetupConstants.ManifestFileName),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(false));
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string ManifestHash(string directory) =>
        Hash(Path.Combine(directory, ViewerSetupConstants.ManifestFileName));

    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class FakeProcessManager : IViewerProcessManager
{
    public bool SmokeSucceeds { get; set; } = true;
    public bool LaunchSucceeds { get; set; } = true;
    public int SmokeCalls { get; private set; }
    public int LaunchCalls { get; private set; }

    public Task<ViewerProcessCheckResult> RunSmokeCheckAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        SmokeCalls++;
        return Task.FromResult(new ViewerProcessCheckResult(
            SmokeSucceeds,
            SmokeSucceeds
                ? ViewerSetupErrorCodes.Ok
                : ViewerSetupErrorCodes.SmokeFailed));
    }

    public Task<ViewerProcessCheckResult> LaunchAndVerifyAsync(
        string executablePath,
        TimeSpan livenessWindow,
        CancellationToken cancellationToken)
    {
        LaunchCalls++;
        return Task.FromResult(new ViewerProcessCheckResult(
            LaunchSucceeds,
            LaunchSucceeds
                ? ViewerSetupErrorCodes.Ok
                : ViewerSetupErrorCodes.LaunchFailed));
    }
}

internal sealed class FakeShutdownCoordinator : IViewerShutdownCoordinator
{
    public ViewerShutdownStatus Status { get; set; } =
        ViewerShutdownStatus.AlreadyStopped;
    public int Calls { get; private set; }

    public Task<ViewerShutdownResult> EnsureStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new ViewerShutdownResult(Status));
    }
}

internal sealed class FakeShortcutManager(IViewerSetupFileSystem fileSystem)
    : IViewerShortcutManager
{
    public bool FailDesktopCreate { get; set; }
    public bool FailRestore { get; set; }

    public ShortcutJournalSnapshot Capture(
        string shortcutPath,
        string backupFilePath,
        string expectedTargetPath)
    {
        var existed = fileSystem.FileExists(shortcutPath);
        if (existed)
        {
            fileSystem.WriteAllBytesAtomic(
                backupFilePath,
                fileSystem.ReadAllBytes(shortcutPath));
        }

        return new ShortcutJournalSnapshot(
            shortcutPath,
            existed,
            backupFilePath,
            expectedTargetPath);
    }

    public ViewerShortcutMutationResult Create(
        string shortcutPath,
        string targetPath,
        string workingDirectory)
    {
        if (FailDesktopCreate && shortcutPath.Contains("desktop", StringComparison.Ordinal))
        {
            throw new IOException("synthetic shortcut failure");
        }

        if (fileSystem.FileExists(shortcutPath) && IsUnowned(shortcutPath))
        {
            return new ViewerShortcutMutationResult(
                ViewerShortcutMutationStatus.PreservedUnowned);
        }

        var existed = fileSystem.FileExists(shortcutPath);
        fileSystem.WriteAllTextAtomic(shortcutPath, "owned:" + targetPath);
        return new ViewerShortcutMutationResult(
            existed
                ? ViewerShortcutMutationStatus.UpdatedOwned
                : ViewerShortcutMutationStatus.Created);
    }

    public ViewerShortcutMutationResult RemoveOwned(
        string shortcutPath,
        string expectedTargetPath)
    {
        if (!fileSystem.FileExists(shortcutPath))
        {
            return new ViewerShortcutMutationResult(
                ViewerShortcutMutationStatus.Missing);
        }

        if (IsUnowned(shortcutPath))
        {
            return new ViewerShortcutMutationResult(
                ViewerShortcutMutationStatus.PreservedUnowned);
        }

        fileSystem.DeleteFile(shortcutPath);
        return new ViewerShortcutMutationResult(
            ViewerShortcutMutationStatus.RemovedOwned);
    }

    public void Restore(ShortcutJournalSnapshot snapshot)
    {
        if (FailRestore)
        {
            throw new IOException("synthetic shortcut restore failure");
        }

        if (fileSystem.FileExists(snapshot.ShortcutPath) &&
            !string.Equals(
                fileSystem.ReadAllText(snapshot.ShortcutPath),
                "owned:" + snapshot.ExpectedTargetPath,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!snapshot.Existed)
        {
            fileSystem.DeleteFile(snapshot.ShortcutPath);
            return;
        }

        fileSystem.WriteAllBytesAtomic(
            snapshot.ShortcutPath,
            fileSystem.ReadAllBytes(snapshot.BackupFilePath));
    }

    private bool IsUnowned(string path) =>
        fileSystem.ReadAllText(path).StartsWith("unowned:", StringComparison.Ordinal);
}

internal sealed class NoOpDeploymentLock : IViewerDeploymentLock
{
    public IDisposable Acquire() => new Lease();

    private sealed class Lease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
