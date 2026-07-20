using SamsungSwitchWatch.Core.Models;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Core.Parsing;

public static class SamsungSnapshotParser
{
    public static DeviceSnapshot Parse(
        string deviceId,
        IEnumerable<CommandOutput> outputs,
        DateTimeOffset collectedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(outputs);

        VersionSnapshot? version = null;
        SystemSnapshot? system = null;
        LogSnapshot? logs = null;
        InterfaceStatusSnapshot? interfaces = null;
        var issues = new List<CollectorIssue>();

        foreach (var output in outputs)
        {
            switch (output.CommandId)
            {
                case CommandIds.Version:
                    Capture(VersionOutputParser.Parse(output.NormalizedOutput), output.CommandId, issues, value => version = value);
                    break;
                case CommandIds.System:
                    Capture(SystemOutputParser.Parse(output.NormalizedOutput), output.CommandId, issues, value => system = value);
                    break;
                case CommandIds.LogRam:
                    Capture(LogOutputParser.Parse(output.NormalizedOutput), output.CommandId, issues, value => logs = value);
                    break;
                case CommandIds.InterfaceStatus:
                    Capture(InterfaceStatusOutputParser.Parse(output.NormalizedOutput), output.CommandId, issues, value => interfaces = value);
                    break;
            }
        }

        return new DeviceSnapshot(deviceId, collectedAt, version, system, logs, interfaces, issues);
    }

    private static void Capture<T>(
        Diagnostics.ParseResult<T> result,
        string commandId,
        ICollection<CollectorIssue> issues,
        Action<T> assign)
    {
        if (result.IsSuccess)
        {
            assign(result.Value!);
        }
        else if (result.Error is not null)
        {
            issues.Add(new CollectorIssue(commandId, result.Error));
        }
    }
}
