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
    FirewallRuleSnapshot PreviousHttpFirewall)
{
    // Optional diagnostic metadata keeps format 1/2 journals readable. These
    // values are sanitized stable codes/messages only; paths and secrets are
    // never added here.
    public string? PrimaryFailureCode { get; init; }
    public string? PrimaryFailureMessage { get; init; }
    public IReadOnlyList<string> RollbackFailureCodes { get; init; } = [];
    public string? AgentHealthCode { get; init; }
    public bool AgentRestartObserved { get; init; }
    public bool AgentServiceRunningObserved { get; init; }
    public bool AgentListenerOwnedObserved { get; init; }
    public int AgentHttpAttemptCount { get; init; }
    public AgentHealthTransportPhase AgentLastTransportPhase { get; init; }
}

internal sealed class DeploymentJournalCleanupVerificationException
    : Exception
{
    public DeploymentJournalCleanupVerificationException()
        : base("설치 작업 기록이 삭제됐는지 확인할 수 없습니다.")
    {
    }
}

public sealed class DeploymentJournalStore(
    ISetupFileSystem fileSystem,
    DeploymentPaths paths)
{
    public const int LegacyFormatVersion = 1;
    public const int CurrentFormatVersion = 2;

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
                journal.FormatVersion is not (LegacyFormatVersion or CurrentFormatVersion) ||
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
        fileSystem.DeleteFile(JournalPath);

        if (Exists)
        {
            throw new DeploymentJournalCleanupVerificationException();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
