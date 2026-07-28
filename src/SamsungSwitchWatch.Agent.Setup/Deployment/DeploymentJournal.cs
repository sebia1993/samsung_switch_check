using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public sealed record DeploymentJournal(
    int FormatVersion,
    string TransactionId,
    string Stage,
    string PackageVersion,
    string StagingDirectory,
    string BackupDirectory,
    string FailedDirectory,
    bool MutationStarted,
    bool InstallMovedToBackup,
    bool StagingActivated,
    bool DataDirectoryExistedBefore,
    bool DataDirectoryCreated,
    ServiceSnapshot PreviousService,
    FirewallRuleSnapshot PreviousHttpsFirewall,
    FirewallRuleSnapshot PreviousHttpFirewall);

public sealed class DeploymentJournalStore(
    ISetupFileSystem fileSystem,
    DeploymentPaths paths)
{
    public string JournalPath =>
        Path.Combine(paths.OperationsDirectory, "agent-native-setup-transaction.json");

    public bool Exists => fileSystem.FileExists(JournalPath);

    public DeploymentJournal Read()
    {
        try
        {
            var journal = JsonSerializer.Deserialize<DeploymentJournal>(
                fileSystem.ReadAllText(JournalPath),
                JsonOptions);
            if (journal is null ||
                journal.FormatVersion != 1 ||
                string.IsNullOrWhiteSpace(journal.TransactionId) ||
                string.IsNullOrWhiteSpace(journal.Stage) ||
                journal.PreviousService is null ||
                journal.PreviousHttpsFirewall is null ||
                journal.PreviousHttpFirewall is null)
            {
                throw new JsonException();
            }

            return journal;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 작업 기록을 안전하게 읽을 수 없습니다. 파일을 보존하고 관리자 확인이 필요합니다.",
                exception);
        }
    }

    public void Write(DeploymentJournal journal)
    {
        fileSystem.CreateDirectory(paths.OperationsDirectory);
        fileSystem.EnsureDirectoryAccess(
            paths.OperationsDirectory,
            DirectoryAccessKind.AdministratorOnly);
        fileSystem.WriteAllTextAtomic(
            JournalPath,
            JsonSerializer.Serialize(journal, JsonOptions));
    }

    public void Delete()
    {
        if (Exists)
        {
            fileSystem.DeleteFile(JournalPath);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
