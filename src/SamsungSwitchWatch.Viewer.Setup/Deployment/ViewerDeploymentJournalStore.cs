using System.Text.Json;

namespace SamsungSwitchWatch.Viewer.Setup.Deployment;

public sealed class ViewerDeploymentJournalStore(
    IViewerSetupFileSystem fileSystem,
    ViewerSetupPaths paths)
{
    public const int CurrentFormatVersion = 2;

    public bool Exists => fileSystem.FileExists(paths.JournalPath);

    public ViewerDeploymentJournal Read()
    {
        try
        {
            var journal = JsonSerializer.Deserialize<ViewerDeploymentJournal>(
                fileSystem.ReadAllText(paths.JournalPath),
                JsonOptions);
            if (journal is null)
            {
                throw new JsonException();
            }

            ViewerSetupPathGuard.ValidateJournal(paths, journal);
            return journal;
        }
        catch (ViewerSetupException exception)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RecoveryRequired,
                "이전 Viewer 설치 작업 기록의 경로를 안전하게 확인할 수 없습니다.",
                exception);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RecoveryRequired,
                "이전 Viewer 설치 작업 기록을 안전하게 읽을 수 없습니다.",
                exception);
        }
    }

    public void Write(ViewerDeploymentJournal journal)
    {
        ViewerSetupPathGuard.ValidateJournal(paths, journal);
        fileSystem.CreateDirectory(paths.OperationsDirectory);
        fileSystem.WriteAllTextAtomic(
            paths.JournalPath,
            JsonSerializer.Serialize(journal, JsonOptions));
    }

    public void Delete()
    {
        fileSystem.DeleteFile(paths.JournalPath);
        if (Exists)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.RollbackFailed,
                "Viewer 설치 작업 기록을 정리하지 못했습니다.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
