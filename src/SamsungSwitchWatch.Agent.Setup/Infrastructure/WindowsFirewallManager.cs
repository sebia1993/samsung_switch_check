using System.Runtime.InteropServices;
using Microsoft.Win32;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Infrastructure;

public sealed class WindowsFirewallManager : IFirewallManager
{
    internal const string LegacyPowerShellHttpsFriendlyName =
        "Samsung Switch Watch Agent HTTPS";
    internal const string LegacyPowerShellGroup = "Samsung Switch Watch";
    internal const string FirewallOverlapWarningCode =
        "FIREWALL_OVERLAP_PROTECTED";

    private const int NetFwProfileDomain = 1;
    private const int NetFwProfilePrivate = 2;
    private const int NetFwRuleDirectionIn = 1;
    private const int NetFwActionAllow = 1;
    private const int TcpProtocol = 6;
    private const int AnyProtocol = 256;
    private const int NetFwProfilePublic = 4;
    private const int NetFwActionBlock = 0;

    public FirewallRuleSnapshot Capture(string ruleName)
    {
        EnsureWindows();
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            foreach (var item in rules)
            {
                object? ruleObject = item;
                try
                {
                    dynamic rule = ruleObject;
                    if (!string.Equals(
                            (string)rule.Name,
                            ruleName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return new FirewallRuleSnapshot(
                        true,
                        (string)rule.Name,
                        (string?)rule.Description ?? string.Empty,
                        (bool)rule.Enabled,
                        (int)rule.Direction,
                        (int)rule.Action,
                        (int)rule.Protocol,
                        (string?)rule.LocalPorts ?? string.Empty,
                        (string?)rule.RemoteAddresses ?? string.Empty,
                        (int)rule.Profiles,
                        (string?)rule.InterfaceTypes ?? "All",
                        (bool)rule.EdgeTraversal,
                        (string?)rule.Grouping ?? string.Empty)
                    {
                        ApplicationName = (string?)rule.ApplicationName ??
                                          string.Empty,
                        ServiceName = (string?)rule.ServiceName ?? string.Empty
                    };
                }
                finally
                {
                    ReleaseCom(ruleObject);
                }
            }

            return FirewallRuleSnapshot.Missing(ruleName);
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "Windows 방화벽 상태를 확인하지 못했습니다.",
                exception);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    public void ApplyViewerRule(string ruleName, int port, string viewerIpv4)
    {
        EnsureWindows();
        RemoveOwnedRule(ruleName);

        object? policyObject = null;
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            ruleObject = CreateComObject("HNetCfg.FWRule");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            dynamic rule = ruleObject;
            rule.Name = ruleName;
            rule.Description = "Owned by SamsungSwitchWatchAgent native setup v2";
            rule.Protocol = TcpProtocol;
            rule.LocalPorts = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.RemoteAddresses = viewerIpv4.Contains(',', StringComparison.Ordinal) ||
                                   viewerIpv4.Contains('/', StringComparison.Ordinal)
                ? viewerIpv4
                : $"{viewerIpv4}/32";
            rule.Direction = NetFwRuleDirectionIn;
            rule.Enabled = true;
            rule.Profiles = NetFwProfileDomain | NetFwProfilePrivate;
            rule.InterfaceTypes = "All";
            rule.EdgeTraversal = false;
            rule.Action = NetFwActionAllow;
            rules.Add(rule);
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "Viewer 전용 Windows 방화벽 규칙을 적용하지 못했습니다.",
                exception);
        }
        finally
        {
            ReleaseCom(ruleObject);
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    public void RemoveOwnedRule(string ruleName)
    {
        EnsureWindows();
        var existing = Capture(ruleName);
        if (!existing.Exists)
        {
            return;
        }

        if (!IsOwnedRule(existing))
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "같은 이름의 비소유 방화벽 규칙이 있어 안전을 위해 설치를 중단했습니다.");
        }

        RemoveRuleByName(ruleName);
    }

    private static void RemoveRuleByName(string ruleName)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            try
            {
                rules.Remove(ruleName);
            }
            catch (COMException exception) when (
                unchecked((uint)exception.HResult) is 0x80070002 or 0x80070490)
            {
                // The exact owned rule does not exist.
            }
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "기존 Agent 방화벽 규칙을 정리하지 못했습니다.",
                exception);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    public void Restore(FirewallRuleSnapshot snapshot)
    {
        var current = Capture(snapshot.Name);
        if (current == snapshot)
        {
            return;
        }

        RemoveOwnedRule(snapshot.Name);
        if (!snapshot.Exists)
        {
            return;
        }

        object? policyObject = null;
        object? rulesObject = null;
        object? ruleObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            ruleObject = CreateComObject("HNetCfg.FWRule");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            dynamic rule = ruleObject;
            rule.Name = snapshot.Name;
            rule.Description = snapshot.Description;
            rule.Protocol = snapshot.Protocol;
            if (!string.IsNullOrWhiteSpace(snapshot.LocalPorts))
            {
                rule.LocalPorts = snapshot.LocalPorts;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.RemoteAddresses))
            {
                rule.RemoteAddresses = snapshot.RemoteAddresses;
            }
            rule.Direction = snapshot.Direction;
            rule.Enabled = snapshot.Enabled;
            rule.Profiles = snapshot.Profiles;
            rule.InterfaceTypes = snapshot.InterfaceTypes;
            rule.EdgeTraversal = snapshot.EdgeTraversal;
            rule.Action = snapshot.Action;
            if (!string.IsNullOrWhiteSpace(snapshot.Grouping))
            {
                rule.Grouping = snapshot.Grouping;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.ApplicationName))
            {
                rule.ApplicationName = snapshot.ApplicationName;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.ServiceName))
            {
                rule.ServiceName = snapshot.ServiceName;
            }
            rules.Add(rule);
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "이전 Windows 방화벽 규칙을 복구하지 못했습니다.",
                exception);
        }
        finally
        {
            ReleaseCom(ruleObject);
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    public bool IsExactViewerRule(string ruleName, int port, string viewerIpv4)
    {
        var snapshot = Capture(ruleName);
        return string.Equals(
                viewerIpv4,
                SetupConstants.PrivateNetworkFirewallRemoteAddresses,
                StringComparison.Ordinal)
            ? FirewallRuleVerifier.EvaluatePrivateNetworks(snapshot, port).IsExact
            : FirewallRuleVerifier.Evaluate(snapshot, port, viewerIpv4).IsExact;
    }

    public FirewallSecurityAssessment AssertSecurityGate(
        int port,
        string agentExecutablePath)
    {
        EnsureWindows();
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            var firewallServiceRunning =
                WindowsServiceManager.IsServiceRunningReadOnly("MpsSvc");
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            var activeProfiles = (int)policy.CurrentProfileTypes;
            var evaluatedProfiles =
                activeProfiles &
                (NetFwProfileDomain | NetFwProfilePrivate | NetFwProfilePublic);
            var profileStates = new List<FirewallProfileSecurityState>();
            foreach (var profile in new[]
                     {
                         NetFwProfileDomain,
                         NetFwProfilePrivate,
                         NetFwProfilePublic
                     })
            {
                if ((evaluatedProfiles & profile) == 0)
                {
                    continue;
                }

                profileStates.Add(new FirewallProfileSecurityState(
                    profile,
                    (bool)policy.FirewallEnabled[profile],
                    (int)policy.DefaultInboundAction[profile],
                    profile == NetFwProfilePublic ||
                    AllowsLocalFirewallRules(profile)));
            }

            AssertPolicySecurityGate(
                firewallServiceRunning,
                activeProfiles,
                profileStates);

            var externalOverlapCount = 0;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            foreach (var item in rules)
            {
                object? ruleObject = item;
                try
                {
                    dynamic rule = ruleObject;
                    var protocol = (int)rule.Protocol;
                    var localPorts = protocol is TcpProtocol or AnyProtocol
                        ? (string?)rule.LocalPorts
                        : string.Empty;
                    var snapshot = new FirewallRuleSnapshot(
                        true,
                        (string)rule.Name,
                        (string?)rule.Description ?? string.Empty,
                        (bool)rule.Enabled,
                        (int)rule.Direction,
                        (int)rule.Action,
                        protocol,
                        localPorts ?? string.Empty,
                        (string?)rule.RemoteAddresses ?? string.Empty,
                        (int)rule.Profiles,
                        (string?)rule.InterfaceTypes ?? "All",
                        (bool)rule.EdgeTraversal,
                        (string?)rule.Grouping ?? string.Empty)
                    {
                        ApplicationName = (string?)rule.ApplicationName ??
                                          string.Empty,
                        ServiceName = (string?)rule.ServiceName ?? string.Empty
                    };

                    var disposition = ClassifyRuleForSecurity(
                        snapshot,
                        port,
                        evaluatedProfiles,
                        agentExecutablePath);
                    if (disposition == FirewallRuleDisposition.ProductNameCollision)
                    {
                        throw new SetupException(
                            SetupErrorCodes.FirewallFailed,
                            "Agent 전용 이름을 사용하는 비소유 방화벽 규칙이 있어 안전을 위해 설치를 중단했습니다.");
                    }

                    if (disposition == FirewallRuleDisposition.ExternalOverlap)
                    {
                        externalOverlapCount++;
                    }
                }
                finally
                {
                    ReleaseCom(ruleObject);
                }
            }

            return externalOverlapCount == 0
                ? FirewallSecurityAssessment.Safe
                : new FirewallSecurityAssessment(
                [
                    new FirewallSecurityWarning(
                        FirewallOverlapWarningCode,
                        $"TCP/{port}을 허용하는 다른 인바운드 방화벽 규칙 {externalOverlapCount}개가 있습니다. " +
                        "해당 규칙은 변경하지 않으며 Agent 업무 API는 loopback과 RFC1918 사설 IPv4 요청만 허용합니다.")
                ]);
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "Windows 방화벽 보안 조건을 확인하지 못했습니다.",
                exception);
        }
        finally
        {
            ReleaseCom(rulesObject);
            ReleaseCom(policyObject);
        }
    }

    internal static void AssertPolicySecurityGate(
        bool firewallServiceRunning,
        int activeProfiles,
        IReadOnlyList<FirewallProfileSecurityState> profileStates)
    {
        if (!firewallServiceRunning)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "Windows 방화벽 서비스가 실행 중이 아니어서 Agent를 안전하게 열 수 없습니다.");
        }

        var supportedProfiles =
            activeProfiles & (NetFwProfileDomain | NetFwProfilePrivate);
        if (supportedProfiles == 0)
        {
            throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "활성 네트워크가 Domain 또는 Private 프로필이 아닙니다.");
        }

        foreach (var state in profileStates)
        {
            if (!state.FirewallEnabled ||
                state.DefaultInboundAction != NetFwActionBlock)
            {
                throw new SetupException(
                    SetupErrorCodes.FirewallFailed,
                    "활성 네트워크의 Windows 방화벽 또는 기본 인바운드 차단 정책이 비활성화되어 있습니다.");
            }

            if (state.Profile != NetFwProfilePublic &&
                !state.AllowsLocalFirewallRules)
            {
                throw new SetupException(
                    SetupErrorCodes.FirewallFailed,
                    "그룹 정책이 로컬 방화벽 규칙 병합을 차단하고 있어 제품의 사설 Viewer 대역 규칙을 보장할 수 없습니다.");
            }
        }
    }

    internal static FirewallRuleDisposition ClassifyRuleForSecurity(
        FirewallRuleSnapshot snapshot,
        int port,
        int evaluatedProfiles,
        string agentExecutablePath)
    {
        if ((string.Equals(
                 snapshot.Name,
                 SetupConstants.FirewallRuleName,
                 StringComparison.Ordinal) ||
             string.Equals(
                 snapshot.Name,
                 LegacyPowerShellHttpsFriendlyName,
                 StringComparison.Ordinal)) &&
            !IsOwnedRule(snapshot))
        {
            return FirewallRuleDisposition.ProductNameCollision;
        }

        if (!snapshot.Enabled ||
            snapshot.Direction != NetFwRuleDirectionIn ||
            snapshot.Action != NetFwActionAllow ||
            (snapshot.Profiles & evaluatedProfiles) == 0)
        {
            return FirewallRuleDisposition.Ignored;
        }

        if (snapshot.Protocol != AnyProtocol &&
            (snapshot.Protocol != TcpProtocol ||
             !PortSpecificationIncludes(snapshot.LocalPorts, port)))
        {
            return FirewallRuleDisposition.Ignored;
        }

        if (!RuleMayApplyToAgent(
                snapshot.ApplicationName,
                snapshot.ServiceName,
                agentExecutablePath,
                SetupConstants.ServiceName))
        {
            return FirewallRuleDisposition.Ignored;
        }

        return IsOwnedRule(snapshot)
            ? FirewallRuleDisposition.Owned
            : FirewallRuleDisposition.ExternalOverlap;
    }

    internal static bool PortSpecificationIncludes(string? specification, int port)
    {
        if (string.IsNullOrWhiteSpace(specification) ||
            specification.Trim() is "*" or "Any")
        {
            return true;
        }

        foreach (var entry in specification.Split(','))
        {
            var value = entry.Trim();
            if (int.TryParse(value, out var exact) && exact == port)
            {
                return true;
            }

            var range = value.Split('-');
            if (range.Length == 2 &&
                int.TryParse(range[0].Trim(), out var start) &&
                int.TryParse(range[1].Trim(), out var end) &&
                port >= start &&
                port <= end)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool RuleMayApplyToAgent(
        string? applicationName,
        string? serviceName,
        string agentExecutablePath,
        string agentServiceName)
    {
        var applicationMatches = ApplicationScopeMayMatch(
            applicationName,
            agentExecutablePath);
        var serviceMatches =
            string.IsNullOrWhiteSpace(serviceName) ||
            serviceName.Trim() is "*" or "Any" ||
            string.Equals(
                serviceName.Trim(),
                agentServiceName,
                StringComparison.OrdinalIgnoreCase);
        return applicationMatches && serviceMatches;
    }

    private static bool ApplicationScopeMayMatch(
        string? applicationName,
        string agentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(applicationName) ||
            applicationName.Trim() is "*" or "Any")
        {
            return true;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(
                    applicationName.Trim())
                .Trim('"');
            return string.Equals(
                Path.GetFullPath(expanded),
                Path.GetFullPath(agentExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable program scope cannot safely prove that the rule is
            // unrelated to the Agent, so treat it as potentially applicable.
            return true;
        }
    }

    private static bool AllowsLocalFirewallRules(int profile)
    {
        var profileName = profile == NetFwProfileDomain
            ? "DomainProfile"
            : "StandardProfile";
        using var policyKey = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{profileName}",
            writable: false);
        var configured = policyKey?.GetValue("AllowLocalPolicyMerge");
        return configured is null || Convert.ToInt32(configured) != 0;
    }

    internal static bool IsOwnedRule(FirewallRuleSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            return false;
        }

        return snapshot.Name switch
        {
            SetupConstants.FirewallRuleName =>
                snapshot.Description is
                    "Owned by SamsungSwitchWatchAgent native setup v2" or
                    "Owned by SamsungSwitchWatchAgent native setup v1" or
                    "Owned by SamsungSwitchWatchAgent installer v3" or
                    "Owned by SamsungSwitchWatchAgent installer v1" &&
                HasExpectedOwnedRuleStructure(snapshot, SetupConstants.HttpsPort) &&
                HasExpectedProductScope(snapshot),
            SetupConstants.LegacyFirewallRuleName =>
                snapshot.Description == "Owned by SamsungSwitchWatchAgent installer v2",
            LegacyPowerShellHttpsFriendlyName =>
                snapshot.Description is
                    "Owned by SamsungSwitchWatchAgent installer v3" or
                    "Owned by SamsungSwitchWatchAgent installer v1" &&
                string.Equals(
                    snapshot.Grouping,
                    LegacyPowerShellGroup,
                    StringComparison.Ordinal) &&
                HasExpectedOwnedRuleStructure(snapshot, SetupConstants.HttpsPort) &&
                HasExpectedProductScope(snapshot),
            _ => false
        };
    }

    private static bool HasExpectedOwnedRuleStructure(
        FirewallRuleSnapshot snapshot,
        int port) =>
        snapshot.Direction == NetFwRuleDirectionIn &&
        snapshot.Action == NetFwActionAllow &&
        snapshot.Protocol == TcpProtocol &&
        string.Equals(
            snapshot.LocalPorts,
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal) &&
        snapshot.Profiles == (NetFwProfileDomain | NetFwProfilePrivate) &&
        string.Equals(snapshot.InterfaceTypes, "All", StringComparison.OrdinalIgnoreCase) &&
        !snapshot.EdgeTraversal;

    private static bool HasExpectedProductScope(FirewallRuleSnapshot snapshot) =>
        IsAnyScope(snapshot.ApplicationName) &&
        IsAnyScope(snapshot.ServiceName);

    private static bool IsAnyScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim() is "*" or "Any";

    private static object CreateComObject(string programId)
    {
        var type = Type.GetTypeFromProgID(programId, throwOnError: false);
        return type is null
            ? throw new SetupException(
                SetupErrorCodes.FirewallFailed,
                "Windows 방화벽 관리 구성 요소를 찾지 못했습니다.")
            : Activator.CreateInstance(type) ??
              throw new SetupException(
                  SetupErrorCodes.FirewallFailed,
                  "Windows 방화벽 관리 구성 요소를 시작하지 못했습니다.");
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
    }
}

internal sealed record FirewallProfileSecurityState(
    int Profile,
    bool FirewallEnabled,
    int DefaultInboundAction,
    bool AllowsLocalFirewallRules);

internal enum FirewallRuleDisposition
{
    Ignored,
    Owned,
    ExternalOverlap,
    ProductNameCollision
}
