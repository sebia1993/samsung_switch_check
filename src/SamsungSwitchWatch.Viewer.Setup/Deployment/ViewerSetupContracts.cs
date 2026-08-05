using System.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup.Deployment;

public static class ViewerSetupConstants
{
    public const string ProductName = "SamsungSwitchWatch";
    public const string ViewerExecutableName = "SamsungSwitchWatch.Viewer.exe";
    public const string SetupExecutableName = "SamsungSwitchWatch.Viewer.Setup.exe";
    public const string ManifestFileName = "BUILD-MANIFEST.json";
    public const string ShortcutFileName = "Samsung Switch Watch.lnk";
    public const string InstallSmokeArgument = "--install-smoke-check";
}

public static class ViewerSetupErrorCodes
{
    public const string Ok = "OK";
    public const string PackageInvalid = "VIEWER_SETUP_PACKAGE_INVALID";
    public const string InstallWriteFailed = "VIEWER_SETUP_INSTALL_WRITE_FAILED";
    public const string PackageNotFound = "VIEWER_SETUP_PACKAGE_NOT_FOUND";
    public const string ManifestInvalid = "VIEWER_SETUP_MANIFEST_INVALID";
    public const string PackageHashMismatch = "VIEWER_SETUP_PACKAGE_HASH_MISMATCH";
    public const string PathInvalid = "VIEWER_SETUP_PATH_INVALID";
    public const string PathNotWritable = "VIEWER_SETUP_PATH_NOT_WRITABLE";
    public const string AlreadyRunning = "VIEWER_SETUP_ALREADY_RUNNING";
    public const string RecoveryRequired = "VIEWER_SETUP_RECOVERY_REQUIRED";
    public const string ViewerRunning = "VIEWER_SETUP_VIEWER_RUNNING";
    public const string SmokeFailed = "VIEWER_SETUP_SMOKE_FAILED";
    public const string LaunchFailed = "VIEWER_SETUP_LAUNCH_FAILED";
    public const string ShortcutFailed = "VIEWER_SETUP_SHORTCUT_FAILED";
    public const string RollbackFailed = "VIEWER_SETUP_ROLLBACK_FAILED";
    public const string Cancelled = "VIEWER_SETUP_CANCELLED";
    public const string Unexpected = "VIEWER_SETUP_UNEXPECTED";
}

public sealed class ViewerSetupException(
    string code,
    string safeMessage,
    Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string Code { get; } = code;
}

public sealed record ViewerSetupPaths(
    string PackageDirectory,
    string InstallDirectory,
    string DataDirectory,
    string OperationsDirectory,
    string DesktopShortcutPath,
    string StartMenuShortcutPath,
    string StartupShortcutPath)
{
    public static ViewerSetupPaths ForCurrentUser(string packageDirectory)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);

        var dataDirectory = Path.Combine(localAppData, "SamsungSwitchWatch");
        return new ViewerSetupPaths(
            Path.GetFullPath(packageDirectory),
            Path.Combine(
                localAppData,
                "Programs",
                "SamsungSwitchWatch",
                "Viewer"),
            dataDirectory,
            Path.Combine(dataDirectory, "Setup"),
            Path.Combine(desktop, ViewerSetupConstants.ShortcutFileName),
            Path.Combine(
                roamingAppData,
                "Microsoft",
                "Windows",
                "Start Menu",
                "Programs",
                ViewerSetupConstants.ShortcutFileName),
            Path.Combine(
                roamingAppData,
                "Microsoft",
                "Windows",
                "Start Menu",
                "Programs",
                "Startup",
                ViewerSetupConstants.ShortcutFileName));
    }

    public string ViewerExecutablePath =>
        Path.Combine(InstallDirectory, ViewerSetupConstants.ViewerExecutableName);

    public string JournalPath =>
        Path.Combine(OperationsDirectory, "viewer-native-setup-transaction.json");

    public ViewerTransactionPaths CreateTransactionPaths(string transactionId)
    {
        if (!ViewerSetupPathGuard.IsTransactionId(transactionId))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "설치 작업 식별자가 올바르지 않습니다.");
        }

        return new ViewerTransactionPaths(
            $"{InstallDirectory}.__staging_{transactionId}",
            $"{InstallDirectory}.__backup_{transactionId}",
            $"{InstallDirectory}.__failed_{transactionId}",
            Path.Combine(OperationsDirectory, $"viewer-{transactionId}"));
    }
}

public sealed record ViewerTransactionPaths(
    string StagingDirectory,
    string BackupDirectory,
    string FailedDirectory,
    string EvidenceDirectory);

public sealed record ViewerPackageFile(
    string Name,
    string SourcePath,
    string Sha256,
    long Size);

public sealed record ViewerPackage(
    string Version,
    string SourceCommit,
    string ManifestPath,
    string ManifestSha256,
    IReadOnlyList<ViewerPackageFile> VerifiedFiles)
{
    public IReadOnlyList<ViewerPackageFile> InstallFiles =>
        VerifiedFiles;
}

public enum ViewerSetupStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Warning,
    Information
}

public sealed record ViewerSetupStep(
    string Code,
    string Label,
    ViewerSetupStepState State,
    string Message);

public sealed record ViewerSetupResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<ViewerSetupStep> Steps)
{
    public static ViewerSetupResult Success(
        string message,
        IReadOnlyList<ViewerSetupStep> steps) =>
        new(true, ViewerSetupErrorCodes.Ok, message, steps);

    public static ViewerSetupResult Failure(
        string code,
        string message,
        IReadOnlyList<ViewerSetupStep> steps) =>
        new(false, code, message, steps);
}

public sealed record ViewerRecoveryInspection(
    bool Exists,
    bool CanRecover,
    string Code,
    string Message)
{
    public static ViewerRecoveryInspection None { get; } =
        new(
            false,
            false,
            ViewerSetupErrorCodes.Ok,
            "복구가 필요한 이전 설치 작업이 없습니다.");
}

public sealed record ShortcutJournalSnapshot(
    string ShortcutPath,
    bool Existed,
    string BackupFilePath,
    string ExpectedTargetPath);

public sealed record ViewerDeploymentJournal(
    int FormatVersion,
    string TransactionId,
    string Stage,
    string PackageVersion,
    string PackageManifestSha256,
    string? PreviousManifestSha256,
    string StagingDirectory,
    string BackupDirectory,
    string FailedDirectory,
    string EvidenceDirectory,
    bool PreviousInstallExisted,
    bool InstallMovedToBackup,
    bool StagingActivated,
    ShortcutJournalSnapshot DesktopShortcut,
    ShortcutJournalSnapshot StartMenuShortcut,
    ShortcutJournalSnapshot StartupShortcut,
    bool DesktopShortcutMutated,
    bool StartMenuShortcutMutated,
    bool StartupShortcutMutated,
    bool NormalLaunchObserved);

public sealed record ViewerProcessCheckResult(
    bool Succeeded,
    string Code);

public enum ViewerShutdownStatus
{
    AlreadyStopped,
    Stopped,
    Rejected,
    ProtocolUnsupported,
    Unavailable,
    TimedOut
}

public sealed record ViewerShutdownResult(ViewerShutdownStatus Status)
{
    public bool Succeeded =>
        Status is ViewerShutdownStatus.AlreadyStopped or ViewerShutdownStatus.Stopped;
}

public enum ViewerShortcutMutationStatus
{
    Created,
    UpdatedOwned,
    RemovedOwned,
    Missing,
    PreservedUnowned,
    OwnershipUnknown
}

public sealed record ViewerShortcutMutationResult(
    ViewerShortcutMutationStatus Status)
{
    public bool Mutated =>
        Status is ViewerShortcutMutationStatus.Created or
            ViewerShortcutMutationStatus.UpdatedOwned or
            ViewerShortcutMutationStatus.RemovedOwned;

    public bool Preserved =>
        Status is ViewerShortcutMutationStatus.PreservedUnowned or
            ViewerShortcutMutationStatus.OwnershipUnknown;
}

public interface IViewerPackageValidator
{
    ViewerPackage Validate(string packageDirectory);

    ViewerPackage ValidateExisting(string installDirectory) =>
        Validate(installDirectory);
}

public interface IViewerSetupFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateTopLevelFiles(string path);
    IReadOnlyList<string> EnumerateTopLevelDirectories(string path);
    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    long GetFileLength(string path);
    string ComputeSha256(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void WriteAllTextAtomic(string path, string contents);
    void WriteAllBytesAtomic(string path, byte[] contents);
    void EnsureDirectoryWritable(string path);
    bool DirectoryHasEntries(string path);
}

public interface IViewerProcessManager
{
    Task<ViewerProcessCheckResult> RunSmokeCheckAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<ViewerProcessCheckResult> LaunchAndVerifyAsync(
        string executablePath,
        TimeSpan livenessWindow,
        CancellationToken cancellationToken);
}

public interface IViewerShutdownCoordinator
{
    Task<ViewerShutdownResult> EnsureStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IViewerShortcutManager
{
    ShortcutJournalSnapshot Capture(
        string shortcutPath,
        string backupFilePath,
        string expectedTargetPath);

    ViewerShortcutMutationResult Create(
        string shortcutPath,
        string targetPath,
        string workingDirectory);

    ViewerShortcutMutationResult RemoveOwned(
        string shortcutPath,
        string expectedTargetPath);

    void Restore(ShortcutJournalSnapshot snapshot);
}

public interface IViewerDeploymentLock
{
    IDisposable Acquire();
}

internal sealed class ViewerSetupStepRecorder : List<ViewerSetupStep>
{
    public void Succeeded(string code, string label, string message) =>
        Add(new ViewerSetupStep(
            code,
            label,
            ViewerSetupStepState.Succeeded,
            message));

    public void Warning(string code, string label, string message) =>
        Add(new ViewerSetupStep(
            code,
            label,
            ViewerSetupStepState.Warning,
            message));

    public void Failed(string code, string label, string message) =>
        Add(new ViewerSetupStep(
            code,
            label,
            ViewerSetupStepState.Failed,
            message));
}

public static class ViewerSetupPathGuard
{
    public static bool IsTransactionId(string? value) =>
        value?.Length == 32 && value.All(Uri.IsHexDigit);

    public static void ValidateTransactionPaths(
        ViewerSetupPaths paths,
        string transactionId,
        ViewerTransactionPaths transaction)
    {
        if (!IsTransactionId(transactionId))
        {
            ThrowInvalid();
        }

        var expected = paths.CreateTransactionPaths(transactionId);
        AssertSame(expected.StagingDirectory, transaction.StagingDirectory);
        AssertSame(expected.BackupDirectory, transaction.BackupDirectory);
        AssertSame(expected.FailedDirectory, transaction.FailedDirectory);
        AssertSame(expected.EvidenceDirectory, transaction.EvidenceDirectory);

        var install = Full(paths.InstallDirectory);
        var package = Full(paths.PackageDirectory);
        var candidates = new[]
        {
            Full(transaction.StagingDirectory),
            Full(transaction.BackupDirectory),
            Full(transaction.FailedDirectory),
            Full(transaction.EvidenceDirectory)
        };

        if (candidates.Any(candidate =>
                string.Equals(candidate, install, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, package, StringComparison.OrdinalIgnoreCase)))
        {
            ThrowInvalid();
        }
    }

    public static void ValidateJournal(
        ViewerSetupPaths paths,
        ViewerDeploymentJournal journal)
    {
        if (journal.FormatVersion != ViewerDeploymentJournalStore.CurrentFormatVersion ||
            !IsTransactionId(journal.TransactionId) ||
            string.IsNullOrWhiteSpace(journal.Stage) ||
            !IsSha256(journal.PackageManifestSha256) ||
            journal.PreviousInstallExisted !=
            IsSha256(journal.PreviousManifestSha256))
        {
            ThrowInvalid();
        }

        var transaction = new ViewerTransactionPaths(
            journal.StagingDirectory,
            journal.BackupDirectory,
            journal.FailedDirectory,
            journal.EvidenceDirectory);
        ValidateTransactionPaths(paths, journal.TransactionId, transaction);

        var expectedEvidence = Full(journal.EvidenceDirectory);
        ValidateShortcutSnapshot(
            journal.DesktopShortcut,
            paths.DesktopShortcutPath,
            expectedEvidence,
            paths.ViewerExecutablePath);
        ValidateShortcutSnapshot(
            journal.StartMenuShortcut,
            paths.StartMenuShortcutPath,
            expectedEvidence,
            paths.ViewerExecutablePath);
        ValidateShortcutSnapshot(
            journal.StartupShortcut,
            paths.StartupShortcutPath,
            expectedEvidence,
            paths.ViewerExecutablePath);
    }

    private static void ValidateShortcutSnapshot(
        ShortcutJournalSnapshot snapshot,
        string expectedShortcut,
        string evidenceDirectory,
        string expectedTarget)
    {
        AssertSame(expectedShortcut, snapshot.ShortcutPath);
        AssertSame(expectedTarget, snapshot.ExpectedTargetPath);
        var backup = Full(snapshot.BackupFilePath);
        var parent = Path.GetDirectoryName(backup);
        if (!string.Equals(
                Full(parent ?? string.Empty),
                evidenceDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            ThrowInvalid();
        }
    }

    private static void AssertSame(string expected, string actual)
    {
        if (!string.Equals(
                Full(expected),
                Full(actual),
                StringComparison.OrdinalIgnoreCase))
        {
            ThrowInvalid();
        }
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "Viewer 설치 경로가 올바르지 않습니다.",
                exception);
        }
    }

    private static void ThrowInvalid() =>
        throw new ViewerSetupException(
            ViewerSetupErrorCodes.PathInvalid,
            "이전 설치 작업의 경로를 안전하게 확인할 수 없습니다.");
}
