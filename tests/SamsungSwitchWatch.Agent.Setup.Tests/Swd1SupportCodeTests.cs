using SamsungSwitchWatch.Support;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class Swd1SupportCodeTests
{
    [Fact]
    public void AgentPayload_RoundTripsAllAllocatedFields()
    {
        var payload = CreateAgentPayload();

        var code = Swd1SupportCode.Encode(payload);
        var succeeded = Swd1SupportCode.TryDecode(code, out var decoded);

        Assert.True(succeeded);
        Assert.Equal(payload, decoded);
        Assert.Equal("RECOVERY", decoded!.Common.OperationName);
        Assert.Equal(
            "SETUP_ROLLBACK_FAILED",
            decoded.Common.ResultCodeName);
        Assert.Equal(
            "ROLLBACK_JOURNAL_CLEANUP_FAILED",
            decoded.Common.PrimaryCodeName);
    }

    [Fact]
    public void ViewerPayload_RoundTripsAllAllocatedFields()
    {
        var payload = CreateViewerPayload();

        var code = Swd1SupportCode.Encode(payload);
        var succeeded = Swd1SupportCode.TryDecode(code, out var decoded);

        Assert.True(succeeded);
        Assert.Equal(payload, decoded);
        Assert.Equal(
            "AGENT_CONNECTION_CHECK",
            decoded!.Common.OperationName);
        Assert.Equal(
            "AGENT_CONNECTION_REFUSED",
            decoded.Common.ResultCodeName);
        Assert.Equal(2, decoded.Viewer!.Value.CandidateCount);
        Assert.Equal(3, decoded.Viewer.Value.ApiVersion);
    }

    [Fact]
    public void AgentPayload_MatchesStableGoldenVector()
    {
        var code = Swd1SupportCode.Encode(CreateAgentPayload());

        Assert.Equal("SWD1-0184-WYXJ-01PK-444X", code);
        Assert.Equal(24, code.Length);
        Assert.DoesNotContain('\0', code);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(CreateAgentPayload(), decoded);
    }

    [Fact]
    public void ViewerPayload_MatchesStableGoldenVector()
    {
        var code = Swd1SupportCode.Encode(CreateViewerPayload());

        Assert.Equal("SWD1-G184-M63Q-B081-8JXM", code);
        Assert.Equal(24, code.Length);
        Assert.DoesNotContain('\0', code);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(CreateViewerPayload(), decoded);
    }

    [Fact]
    public void Encode_IsDeterministicAcrossRepeatedStackAllocations()
    {
        var payload = CreateAgentPayload();
        const string golden = "SWD1-0184-WYXJ-01PK-444X";

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var code = Swd1SupportCode.Encode(payload);
            Assert.Equal(golden, code);
            Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
            Assert.Equal(payload, decoded);
        }
    }

    [Fact]
    public void Decode_IgnoresCaseSpacesHyphensAndCrockfordAliases()
    {
        var canonical = Swd1SupportCode.Encode(CreateAgentPayload());
        var body = canonical[5..].Replace("-", string.Empty);
        Assert.Contains('0', body);
        Assert.True(body.Count(character => character == '1') >= 2);

        var aliasedCharacters = body.ToLowerInvariant().ToCharArray();
        var oneCount = 0;
        for (var index = 0; index < aliasedCharacters.Length; index++)
        {
            aliasedCharacters[index] = aliasedCharacters[index] switch
            {
                '0' => 'o',
                '1' when oneCount++ == 0 => 'i',
                '1' => 'l',
                var character => character
            };
        }

        var aliased = new string(aliasedCharacters);
        var input =
            $"  swd1  - {aliased[..4]}  {aliased[4..8]}-" +
            $"{aliased[8..12]}  -  {aliased[12..]}  ";

        Assert.True(Swd1SupportCode.TryDecode(input, out var decoded));
        Assert.Equal(CreateAgentPayload(), decoded);
    }

    [Fact]
    public void Decode_RejectsSingleCharacterChecksumTypo()
    {
        var canonical = Swd1SupportCode.Encode(CreateAgentPayload());
        var typo = canonical[..^1] +
                   (canonical[^1] == 'Z' ? 'Y' : 'Z');

        Assert.False(Swd1SupportCode.TryDecode(typo, out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0184-WYXJ-01PJ-R84C")]
    [InlineData("SWE1-0184-WYXJ-01PJ-R84C")]
    [InlineData("SWD1-0184-WYXJ-01PJ-R84")]
    [InlineData("SWD1-0184-WYXJ-01PJ-R84CC")]
    [InlineData("SWD1-0184-WYXJ-01PJ-R84U")]
    public void Decode_RejectsInvalidPrefixLengthOrAlphabet(string? value)
    {
        Assert.False(Swd1SupportCode.TryDecode(value, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void Builders_UseExplicitUnknownSentinelsForUnmappedValues()
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "15.255.255",
            "private-operation",
            "private-result",
            "private-primary",
            ["private-rollback"],
            "private-journal",
            "private-service",
            "private-tcp",
            "private-readiness",
            "private-package",
            ["private-firewall"],
            reserved: 15);

        var code = Swd1SupportCode.Encode(payload);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));

        Assert.True(decoded!.Common.ProductVersion.IsUnknown);
        Assert.Equal("UNKNOWN", decoded.Common.OperationName);
        Assert.Equal("UNKNOWN", decoded.Common.ResultCodeName);
        Assert.Equal("UNKNOWN", decoded.Common.PrimaryCodeName);
        Assert.Equal(
            Swd1AgentRollbackFlags.None,
            decoded.Agent!.Value.RollbackFlags);
        Assert.Equal(
            Swd1AgentJournalState.Unknown,
            decoded.Agent.Value.JournalState);
        Assert.Equal(
            Swd1AgentServiceState.Unknown,
            decoded.Agent.Value.ServiceState);
        Assert.Equal(
            Swd1CheckState.Unknown,
            decoded.Agent.Value.LocalTcp18443);
        Assert.Equal(
            Swd1AgentFirewallFlags.None,
            decoded.Agent.Value.FirewallFlags);
        Assert.Equal(15, decoded.Agent.Value.Reserved);
    }

    [Fact]
    public void AgentBuilder_TreatsObservedOwnedListenerAsPassed()
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "0.10.13-poc",
            "install",
            "SETUP_HEALTH_FAILED",
            "SETUP_HEALTH_FAILED",
            [],
            "NONE",
            "CONFIGURED",
            "PASS_OBSERVED",
            "FAIL",
            "PASS",
            [],
            reserved: (byte)Swd1AgentHealthCode.HttpsRequestFailed);

        var code = Swd1SupportCode.Encode(payload);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(
            Swd1CheckState.Passed,
            decoded!.Agent!.Value.LocalTcp18443);
    }

    [Fact]
    public void CompactAgentVersion_DistinguishesMaximumNormalValueFromUnknown()
    {
        var normal = CreateViewerPayload(
            agentVersion: "14.62.62",
            candidateCount: 14,
            apiVersion: "6");
        var unknown = CreateViewerPayload(
            agentVersion: "14.63.62",
            candidateCount: 15,
            apiVersion: "7");

        var normalCode = Swd1SupportCode.Encode(normal);
        var unknownCode = Swd1SupportCode.Encode(unknown);

        Assert.NotEqual(normalCode, unknownCode);
        Assert.True(Swd1SupportCode.TryDecode(normalCode, out var normalDecoded));
        Assert.True(Swd1SupportCode.TryDecode(unknownCode, out var unknownDecoded));
        Assert.False(normalDecoded!.Viewer!.Value.AgentVersion.IsUnknown);
        Assert.Equal("14.62.62", normalDecoded.Viewer.Value.AgentVersion.ToString());
        Assert.Equal(14, normalDecoded.Viewer.Value.CandidateCount);
        Assert.Equal(6, normalDecoded.Viewer.Value.ApiVersion);
        Assert.True(unknownDecoded!.Viewer!.Value.AgentVersion.IsUnknown);
        Assert.Null(unknownDecoded.Viewer.Value.CandidateCount);
        Assert.Null(unknownDecoded.Viewer.Value.ApiVersion);
    }

    [Fact]
    public void CommonVersion_DistinguishesMaximumNormalValueFromUnknown()
    {
        var normal = Swd1SemanticVersion.CreateOrUnknown(14, 254, 254);
        var unknown = Swd1SemanticVersion.CreateOrUnknown(15, 254, 254);

        Assert.False(normal.IsUnknown);
        Assert.True(unknown.IsUnknown);
        Assert.NotEqual(normal, unknown);
    }

    [Fact]
    public void Builders_DoNotRetainOrEncodeSecrets()
    {
        const string secret =
            @"DOMAIN\operator:secret-password@10.20.30.40 C:\private";
        var payload = Swd1AgentPayloadBuilder.Build(
            secret,
            secret,
            secret,
            secret,
            [secret],
            secret,
            secret,
            secret,
            secret,
            secret,
            [secret]);

        var code = Swd1SupportCode.Encode(payload);
        Assert.DoesNotContain("DOMAIN", code, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", code, StringComparison.Ordinal);
        Assert.DoesNotContain("password", code, StringComparison.Ordinal);
        Assert.DoesNotContain("10.20.30.40", code, StringComparison.Ordinal);

        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        var decodedText = decoded!.ToString();
        Assert.DoesNotContain("DOMAIN", decodedText, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", decodedText, StringComparison.Ordinal);
        Assert.DoesNotContain("password", decodedText, StringComparison.Ordinal);
        Assert.DoesNotContain("10.20.30.40", decodedText, StringComparison.Ordinal);
        Assert.Equal("UNKNOWN", decoded.Common.ResultCodeName);
    }

    [Fact]
    public void AgentBuilder_MapsStableRollbackAndFirewallMasks()
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "0.10.9-poc",
            "recovery",
            "SETUP_ROLLBACK_FAILED",
            "ROLLBACK_JOURNAL_CLEANUP_FAILED",
            [
                "ROLLBACK_STATE_MISMATCH",
                "ROLLBACK_JOURNAL_CLEANUP_FAILED"
            ],
            "PENDING_BLOCKED",
            "STOPPED",
            "NOT_CONFIRMED",
            "FAIL",
            "PASS",
            [
                "FIREWALL_RULE_DISABLED",
                "FIREWALL_REMOTE_ADDRESS_MISMATCH",
                "FIREWALL_PROFILE_MISMATCH"
            ]);

        Assert.Equal(
            Swd1AgentRollbackFlags.StateMismatch |
            Swd1AgentRollbackFlags.JournalCleanup,
            payload.Agent!.Value.RollbackFlags);
        Assert.Equal(
            Swd1AgentFirewallFlags.Disabled |
            Swd1AgentFirewallFlags.RemoteAddress |
            Swd1AgentFirewallFlags.Profiles,
            payload.Agent.Value.FirewallFlags);
    }

    [Theory]
    [InlineData("FIREWALL_RULE_MISSING", Swd1AgentFirewallFlags.Missing)]
    [InlineData("FIREWALL_RULE_DISABLED", Swd1AgentFirewallFlags.Disabled)]
    [InlineData("FIREWALL_DIRECTION_MISMATCH", Swd1AgentFirewallFlags.Direction)]
    [InlineData("FIREWALL_ACTION_MISMATCH", Swd1AgentFirewallFlags.Action)]
    [InlineData("FIREWALL_PROTOCOL_MISMATCH", Swd1AgentFirewallFlags.Protocol)]
    [InlineData("FIREWALL_PORT_MISMATCH", Swd1AgentFirewallFlags.Port)]
    [InlineData(
        "FIREWALL_REMOTE_ADDRESS_MISMATCH",
        Swd1AgentFirewallFlags.RemoteAddress)]
    [InlineData("FIREWALL_PROFILE_MISMATCH", Swd1AgentFirewallFlags.Profiles)]
    [InlineData(
        "FIREWALL_EDGE_TRAVERSAL_MISMATCH",
        Swd1AgentFirewallFlags.EdgeTraversal)]
    public void AgentBuilder_MapsEachFirewallMismatchToItsStableBit(
        string mismatchCode,
        Swd1AgentFirewallFlags expected)
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "0.10.9-poc",
            "preflight",
            "SETUP_FIREWALL_FAILED",
            "SETUP_FIREWALL_FAILED",
            [],
            "NONE",
            "NOT_INSTALLED",
            "NOT_RUN",
            "NOT_RUN",
            "NOT_RUN",
            [mismatchCode]);

        Assert.Equal(expected, payload.Agent!.Value.FirewallFlags);
    }

    [Fact]
    public void AgentBuilder_DoesNotDuplicateGenericOrRestoreFailuresInFirewallMask()
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "0.10.9-poc",
            "recovery",
            "SETUP_ROLLBACK_FAILED",
            "ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED",
            ["ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED"],
            "PENDING_RECOVERABLE",
            "STOPPED",
            "NOT_RUN",
            "NOT_RUN",
            "NOT_RUN",
            [
                "SETUP_FIREWALL_FAILED",
                "ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED",
                "FIREWALL_EXACT"
            ]);

        Assert.Equal(
            Swd1AgentRollbackFlags.HttpsFirewallRestore,
            payload.Agent!.Value.RollbackFlags);
        Assert.Equal(
            Swd1AgentFirewallFlags.None,
            payload.Agent.Value.FirewallFlags);
    }

    [Fact]
    public void AgentDiagnosticCodePositions_AreAppendOnlyProtocolValues()
    {
        string[] codes =
        [
            "OK",
            "SETUP_PACKAGE_NOT_FOUND",
            "SETUP_MANIFEST_INVALID",
            "SETUP_PACKAGE_HASH_MISMATCH",
            "SETUP_VIEWER_IP_INVALID",
            "SETUP_NETWORK_SELECTION_INVALID",
            "SETUP_EXISTING_NETWORKS_NOT_LOADED",
            "SETUP_ADMINISTRATOR_REQUIRED",
            "SETUP_PATH_INVALID",
            "SETUP_PATH_UNTRUSTED",
            "SETUP_PATH_NOT_WRITABLE",
            "SETUP_CONFIGURATION_INVALID",
            "SETUP_SERVICE_FAILED",
            "SETUP_FIREWALL_FAILED",
            "SETUP_HEALTH_FAILED",
            "SETUP_ROLLBACK_FAILED",
            "SETUP_RECOVERY_REQUIRED",
            "ROLLBACK_STATE_MISMATCH",
            "ROLLBACK_SERVICE_STOP_FAILED",
            "ROLLBACK_FILE_RESTORE_FAILED",
            "ROLLBACK_DATA_CLEANUP_FAILED",
            "ROLLBACK_SERVICE_RESTORE_FAILED",
            "ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED",
            "ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED",
            "ROLLBACK_JOURNAL_WRITE_FAILED",
            "ROLLBACK_EVIDENCE_CLEANUP_FAILED",
            "ROLLBACK_STAGING_CLEANUP_FAILED",
            "ROLLBACK_BACKUP_CLEANUP_FAILED",
            "ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED",
            "ROLLBACK_JOURNAL_CLEANUP_FAILED",
            "SETUP_ALREADY_RUNNING",
            "SETUP_CANCELLED",
            "SETUP_UNEXPECTED",
            "DIAGNOSTIC_WRITE_FAILED"
        ];

        for (var index = 0; index < codes.Length; index++)
        {
            var payload = Swd1AgentPayloadBuilder.Build(
                "0.10.9-poc",
                "preflight",
                codes[index],
                codes[index],
                [],
                "NONE",
                "NOT_INSTALLED",
                "NOT_RUN",
                "NOT_RUN",
                "NOT_RUN",
                []);

            Assert.Equal((byte)index, payload.Common.ResultCode);
            Assert.Equal(codes[index], payload.Common.ResultCodeName);
        }
    }

    [Fact]
    public void ViewerDiagnosticCodePositions_AreAppendOnlyProtocolValues()
    {
        string[] codes =
        [
            "NONE",
            "AGENT_ACCESS_DENIED",
            "AGENT_CLIENT_NOT_ALLOWED",
            "AGENT_CONNECTION_REFUSED",
            "AGENT_DNS_FAILED",
            "AGENT_HTTP_ERROR",
            "AGENT_IDENTITY_CHANGED",
            "AGENT_INTERNAL_ERROR",
            "AGENT_NOT_READY",
            "AGENT_PROTOCOL_MISMATCH",
            "AGENT_RESPONSE_INVALID",
            "AGENT_TIMEOUT",
            "AGENT_UNREACHABLE",
            "AGENT_VERSION_MISMATCH",
            "LOCAL_AGENT_PREFLIGHT_FAILED",
            "LOCAL_AGENT_PREFLIGHT_TIMEOUT",
            "LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED",
            "LOCAL_PRIVATE_IPV4_NOT_FOUND",
            "VIEWER_CONFIGURATION_INVALID",
            "VIEWER_CONNECTION_REQUIRED",
            "VIEWER_SETTINGS_WRITE_FAILED",
            "VIEWER_UNEXPECTED_ERROR"
        ];

        for (var index = 0; index < codes.Length; index++)
        {
            var payload = Swd1ViewerPayloadBuilder.Build(
                "0.10.9-poc",
                "AGENT_CONNECTION_CHECK",
                codes[index],
                codes[index],
                "NORMAL",
                "NONE",
                "NOT_RUN",
                "NOT_RUN",
                "NOT_RUN",
                "NOT_RUN",
                "NOT_RUN",
                0,
                "0.10.9-poc",
                "4");

            Assert.Equal((byte)index, payload.Common.ResultCode);
            Assert.Equal(codes[index], payload.Common.ResultCodeName);
        }
    }

    [Theory]
    [InlineData(
        "ROLLBACK_STATE_MISMATCH",
        Swd1AgentRollbackFlags.StateMismatch)]
    [InlineData(
        "ROLLBACK_SERVICE_STOP_FAILED",
        Swd1AgentRollbackFlags.ServiceStop)]
    [InlineData(
        "ROLLBACK_FILE_RESTORE_FAILED",
        Swd1AgentRollbackFlags.FileRestore)]
    [InlineData(
        "ROLLBACK_DATA_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.DataCleanup)]
    [InlineData(
        "ROLLBACK_SERVICE_RESTORE_FAILED",
        Swd1AgentRollbackFlags.ServiceRestore)]
    [InlineData(
        "ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED",
        Swd1AgentRollbackFlags.HttpsFirewallRestore)]
    [InlineData(
        "ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED",
        Swd1AgentRollbackFlags.LegacyFirewallRestore)]
    [InlineData(
        "ROLLBACK_JOURNAL_WRITE_FAILED",
        Swd1AgentRollbackFlags.JournalWrite)]
    [InlineData(
        "ROLLBACK_EVIDENCE_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.EvidenceCleanup)]
    [InlineData(
        "ROLLBACK_STAGING_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.StagingCleanup)]
    [InlineData(
        "ROLLBACK_BACKUP_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.BackupCleanup)]
    [InlineData(
        "ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.FailedDirectoryCleanup)]
    [InlineData(
        "ROLLBACK_JOURNAL_CLEANUP_FAILED",
        Swd1AgentRollbackFlags.JournalCleanup)]
    public void AgentBuilder_MapsEachRollbackCodeToItsStableBit(
        string rollbackCode,
        Swd1AgentRollbackFlags expected)
    {
        var payload = Swd1AgentPayloadBuilder.Build(
            "0.10.9-poc",
            "recovery",
            "SETUP_ROLLBACK_FAILED",
            rollbackCode,
            [rollbackCode],
            "PENDING_BLOCKED",
            "STOPPED",
            "NOT_RUN",
            "NOT_RUN",
            "NOT_RUN",
            []);

        Assert.Equal(expected, payload.Agent!.Value.RollbackFlags);
    }

    private static Swd1Payload CreateAgentPayload() =>
        Swd1AgentPayloadBuilder.Build(
            "0.10.9-poc",
            "recovery",
            "SETUP_ROLLBACK_FAILED",
            "ROLLBACK_JOURNAL_CLEANUP_FAILED",
            [
                "ROLLBACK_STAGING_CLEANUP_FAILED",
                "ROLLBACK_JOURNAL_CLEANUP_FAILED"
            ],
            "PENDING_RECOVERABLE",
            "STOPPED",
            "NOT_CONFIRMED",
            "FAIL",
            "PASS",
            [
                "FIREWALL_RULE_MISSING",
                "FIREWALL_PORT_MISMATCH",
                "FIREWALL_EDGE_TRAVERSAL_MISMATCH"
            ]);

    private static Swd1Payload CreateViewerPayload(
        string agentVersion = "0.10.9-poc",
        int candidateCount = 2,
        string apiVersion = "3") =>
        Swd1ViewerPayloadBuilder.Build(
            "0.10.9-poc",
            "AGENT_CONNECTION_CHECK",
            "AGENT_CONNECTION_REFUSED",
            "AGENT_CONNECTION_REFUSED",
            "SAME_PC",
            "TCP",
            "SUCCEEDED",
            "SUCCEEDED",
            "FAILED",
            "NOT_RUN",
            "NOT_RUN",
            candidateCount,
            agentVersion,
            apiVersion);
}
