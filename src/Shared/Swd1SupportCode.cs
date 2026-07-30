using System.Globalization;

namespace SamsungSwitchWatch.Support;

public enum Swd1Component : byte
{
    AgentSetup = 0,
    Viewer = 1
}

public readonly record struct Swd1SemanticVersion
{
    private const byte UnknownMajor = 0x0F;
    private const byte UnknownMinorOrPatch = 0xFF;

    private Swd1SemanticVersion(byte major, byte minor, byte patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public byte Major { get; }

    public byte Minor { get; }

    public byte Patch { get; }

    public bool IsUnknown =>
        Major == UnknownMajor &&
        Minor == UnknownMinorOrPatch &&
        Patch == UnknownMinorOrPatch;

    public static Swd1SemanticVersion Unknown { get; } =
        new(UnknownMajor, UnknownMinorOrPatch, UnknownMinorOrPatch);

    public static Swd1SemanticVersion CreateOrUnknown(
        int major,
        int minor,
        int patch) =>
        major is >= 0 and < UnknownMajor &&
        minor is >= 0 and < UnknownMinorOrPatch &&
        patch is >= 0 and < UnknownMinorOrPatch
            ? new Swd1SemanticVersion(
                (byte)major,
                (byte)minor,
                (byte)patch)
            : Unknown;

    public static Swd1SemanticVersion ParseOrUnknown(string? value) =>
        Swd1VersionParser.TryParse(value, out var major, out var minor, out var patch)
            ? CreateOrUnknown(major, minor, patch)
            : Unknown;

    internal static Swd1SemanticVersion FromEncoded(
        byte major,
        byte minor,
        byte patch) =>
        major == UnknownMajor ||
        minor == UnknownMinorOrPatch ||
        patch == UnknownMinorOrPatch
            ? Unknown
            : new Swd1SemanticVersion(major, minor, patch);

    public override string ToString() =>
        IsUnknown
            ? "UNKNOWN"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Major}.{Minor}.{Patch}");
}

public readonly record struct Swd1CompactSemanticVersion
{
    private const byte UnknownMajor = 0x0F;
    private const byte UnknownMinorOrPatch = 0x3F;

    private Swd1CompactSemanticVersion(byte major, byte minor, byte patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public byte Major { get; }

    public byte Minor { get; }

    public byte Patch { get; }

    public bool IsUnknown =>
        Major == UnknownMajor &&
        Minor == UnknownMinorOrPatch &&
        Patch == UnknownMinorOrPatch;

    public static Swd1CompactSemanticVersion Unknown { get; } =
        new(UnknownMajor, UnknownMinorOrPatch, UnknownMinorOrPatch);

    public static Swd1CompactSemanticVersion CreateOrUnknown(
        int major,
        int minor,
        int patch) =>
        major is >= 0 and < UnknownMajor &&
        minor is >= 0 and < UnknownMinorOrPatch &&
        patch is >= 0 and < UnknownMinorOrPatch
            ? new Swd1CompactSemanticVersion(
                (byte)major,
                (byte)minor,
                (byte)patch)
            : Unknown;

    public static Swd1CompactSemanticVersion ParseOrUnknown(string? value) =>
        Swd1VersionParser.TryParse(value, out var major, out var minor, out var patch)
            ? CreateOrUnknown(major, minor, patch)
            : Unknown;

    internal static Swd1CompactSemanticVersion FromEncoded(
        byte major,
        byte minor,
        byte patch) =>
        major == UnknownMajor ||
        minor == UnknownMinorOrPatch ||
        patch == UnknownMinorOrPatch
            ? Unknown
            : new Swd1CompactSemanticVersion(major, minor, patch);

    public override string ToString() =>
        IsUnknown
            ? "UNKNOWN"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Major}.{Minor}.{Patch}");
}

[Flags]
public enum Swd1AgentRollbackFlags : ushort
{
    None = 0,
    StateMismatch = 1 << 0,
    ServiceStop = 1 << 1,
    FileRestore = 1 << 2,
    DataCleanup = 1 << 3,
    ServiceRestore = 1 << 4,
    HttpsFirewallRestore = 1 << 5,
    LegacyFirewallRestore = 1 << 6,
    JournalWrite = 1 << 7,
    EvidenceCleanup = 1 << 8,
    StagingCleanup = 1 << 9,
    BackupCleanup = 1 << 10,
    FailedDirectoryCleanup = 1 << 11,
    JournalCleanup = 1 << 12
}

public enum Swd1AgentJournalState : byte
{
    None = 0,
    PendingRecoverable = 1,
    PendingBlocked = 2,
    Unknown = 3
}

public enum Swd1AgentServiceState : byte
{
    NotInstalled = 0,
    Found = 1,
    Configured = 2,
    Running = 3,
    RunningReady = 4,
    Stopped = 5,
    Failed = 6,
    Unknown = 7
}

public enum Swd1CheckState : byte
{
    NotRun = 0,
    Passed = 1,
    FailedOrNotConfirmed = 2,
    Unknown = 3
}

public enum Swd1AgentHealthCode : byte
{
    NotRecorded = 0,
    ServiceUnavailable = 1,
    ServiceInspectionFailed = 2,
    TcpNotListening = 3,
    TcpOwnedByOtherProcess = 4,
    TcpOwnershipQueryFailed = 5,
    HttpsRequestFailed = 6,
    HttpStatusInvalid = 7,
    PayloadTooLarge = 8,
    PayloadInvalid = 9,
    ApiVersionMismatch = 10,
    ProtocolMismatch = 11,
    ProductVersionMismatch = 12,
    DeadlineExceeded = 13
}

[Flags]
public enum Swd1AgentFirewallFlags : ushort
{
    None = 0,
    Missing = 1 << 0,
    Disabled = 1 << 1,
    Direction = 1 << 2,
    Action = 1 << 3,
    Protocol = 1 << 4,
    Port = 1 << 5,
    RemoteAddress = 1 << 6,
    Profiles = 1 << 7,
    EdgeTraversal = 1 << 8
}

public readonly record struct Swd1AgentTail(
    Swd1AgentRollbackFlags RollbackFlags,
    Swd1AgentJournalState JournalState,
    Swd1AgentServiceState ServiceState,
    Swd1CheckState LocalTcp18443,
    Swd1CheckState Readiness,
    Swd1CheckState PackageValidation,
    Swd1AgentFirewallFlags FirewallFlags,
    byte Reserved = 0)
{
    // The four bits were reserved in SWD1/1. Values 0-13 now carry a safe,
    // non-sensitive readiness failure category while preserving every
    // previously issued code (all earlier producers wrote zero).
    public Swd1AgentHealthCode HealthCode =>
        Enum.IsDefined(typeof(Swd1AgentHealthCode), Reserved)
            ? (Swd1AgentHealthCode)Reserved
            : Swd1AgentHealthCode.NotRecorded;
}

public enum Swd1ViewerMode : byte
{
    Normal = 0,
    SamePc = 1
}

public enum Swd1ViewerFailedStage : byte
{
    None = 0,
    Address = 1,
    Dns = 2,
    Tcp = 3,
    Https = 4,
    Identity = 5,
    Settings = 6,
    Unknown = 7
}

public enum Swd1ViewerStageState : byte
{
    NotRun = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

public readonly record struct Swd1ViewerStageStates(
    Swd1ViewerStageState Address,
    Swd1ViewerStageState Dns,
    Swd1ViewerStageState Tcp,
    Swd1ViewerStageState Https,
    Swd1ViewerStageState Identity);

public readonly record struct Swd1ViewerTail(
    Swd1ViewerMode Mode,
    Swd1ViewerFailedStage FailedStage,
    Swd1ViewerStageStates Stages,
    byte CandidateCountCode,
    Swd1CompactSemanticVersion AgentVersion,
    byte ApiVersionCode)
{
    public int? CandidateCount =>
        CandidateCountCode == Swd1MappingTables.ViewerCandidateUnknown
            ? null
            : CandidateCountCode;

    public int? ApiVersion =>
        ApiVersionCode == Swd1MappingTables.ViewerApiUnknown
            ? null
            : ApiVersionCode;
}

public readonly record struct Swd1CommonFields(
    Swd1Component Component,
    Swd1SemanticVersion ProductVersion,
    byte OperationCode,
    byte ResultCode,
    byte PrimaryCode)
{
    public string OperationName =>
        Swd1MappingTables.OperationName(Component, OperationCode);

    public string ResultCodeName =>
        Swd1MappingTables.DiagnosticCodeName(Component, ResultCode);

    public string PrimaryCodeName =>
        Swd1MappingTables.DiagnosticCodeName(Component, PrimaryCode);
}

public sealed record Swd1Payload
{
    private Swd1Payload(
        Swd1CommonFields common,
        Swd1AgentTail? agent,
        Swd1ViewerTail? viewer)
    {
        Common = common;
        Agent = agent;
        Viewer = viewer;
    }

    public Swd1CommonFields Common { get; }

    public Swd1AgentTail? Agent { get; }

    public Swd1ViewerTail? Viewer { get; }

    public static Swd1Payload ForAgent(
        Swd1CommonFields common,
        Swd1AgentTail tail)
    {
        if (common.Component != Swd1Component.AgentSetup)
        {
            throw new ArgumentException(
                "The common component must be AgentSetup.",
                nameof(common));
        }

        return new Swd1Payload(common, tail, null);
    }

    public static Swd1Payload ForViewer(
        Swd1CommonFields common,
        Swd1ViewerTail tail)
    {
        if (common.Component != Swd1Component.Viewer)
        {
            throw new ArgumentException(
                "The common component must be Viewer.",
                nameof(common));
        }

        return new Swd1Payload(common, null, tail);
    }
}

public static class Swd1AgentPayloadBuilder
{
    public static Swd1Payload Build(
        string? productVersion,
        string? operation,
        string? resultCode,
        string? primaryFailureCode,
        IEnumerable<string>? rollbackFailureCodes,
        string? recoveryJournal,
        string? service,
        string? localTcp18443,
        string? readiness,
        string? packageValidation,
        IEnumerable<string>? firewallDecisionCodes,
        byte reserved = 0)
    {
        var common = new Swd1CommonFields(
            Swd1Component.AgentSetup,
            Swd1SemanticVersion.ParseOrUnknown(productVersion),
            Swd1MappingTables.AgentOperationCode(operation),
            Swd1MappingTables.DiagnosticCode(
                Swd1Component.AgentSetup,
                resultCode),
            Swd1MappingTables.DiagnosticCode(
                Swd1Component.AgentSetup,
                primaryFailureCode ?? resultCode));
        var tail = new Swd1AgentTail(
            Swd1MappingTables.RollbackFlags(rollbackFailureCodes),
            Swd1MappingTables.AgentJournalState(recoveryJournal),
            Swd1MappingTables.AgentServiceState(service),
            Swd1MappingTables.LocalTcpState(localTcp18443),
            Swd1MappingTables.ReadinessState(readiness),
            Swd1MappingTables.PackageState(packageValidation),
            Swd1MappingTables.FirewallFlags(firewallDecisionCodes),
            reserved);
        return Swd1Payload.ForAgent(common, tail);
    }
}

public static class Swd1ViewerPayloadBuilder
{
    public static Swd1Payload Build(
        string? productVersion,
        string? operation,
        string? errorCode,
        string? primaryCode,
        string? mode,
        string? failedStage,
        string? addressState,
        string? dnsState,
        string? tcpState,
        string? httpsState,
        string? identityState,
        int candidateCount,
        string? agentProductVersion,
        string? apiVersion)
    {
        var common = new Swd1CommonFields(
            Swd1Component.Viewer,
            Swd1SemanticVersion.ParseOrUnknown(productVersion),
            Swd1MappingTables.ViewerOperationCode(operation),
            Swd1MappingTables.DiagnosticCode(
                Swd1Component.Viewer,
                errorCode),
            Swd1MappingTables.DiagnosticCode(
                Swd1Component.Viewer,
                primaryCode ?? errorCode));
        var stages = new Swd1ViewerStageStates(
            Swd1MappingTables.ViewerStageState(addressState),
            Swd1MappingTables.ViewerStageState(dnsState),
            Swd1MappingTables.ViewerStageState(tcpState),
            Swd1MappingTables.ViewerStageState(httpsState),
            Swd1MappingTables.ViewerStageState(identityState));
        var tail = new Swd1ViewerTail(
            Swd1MappingTables.ViewerMode(mode),
            Swd1MappingTables.ViewerFailedStage(failedStage),
            stages,
            Swd1MappingTables.ViewerCandidateCode(candidateCount),
            Swd1CompactSemanticVersion.ParseOrUnknown(agentProductVersion),
            Swd1MappingTables.ViewerApiCode(apiVersion));
        return Swd1Payload.ForViewer(common, tail);
    }
}

public static class Swd1SupportCode
{
    private const string Prefix = "SWD1";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int PayloadByteCount = 9;
    private const int EncodedByteCount = 10;
    private const int EncodedCharacterCount = 16;

    public static string Encode(Swd1Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Validate(payload);

        Span<byte> bytes = stackalloc byte[EncodedByteCount];
        bytes.Clear();
        var writer = new Swd1BitWriter(bytes[..PayloadByteCount]);
        WriteCommon(ref writer, payload.Common);

        if (payload.Common.Component == Swd1Component.AgentSetup)
        {
            WriteAgentTail(ref writer, payload.Agent!.Value);
        }
        else
        {
            WriteViewerTail(ref writer, payload.Viewer!.Value);
        }

        if (writer.BitsWritten != PayloadByteCount * 8)
        {
            throw new InvalidOperationException("SWD1 payload must contain 72 bits.");
        }

        bytes[PayloadByteCount] = ComputeCrc8(bytes[..PayloadByteCount]);
        Span<char> encoded = stackalloc char[EncodedCharacterCount];
        for (var index = 0; index < encoded.Length; index++)
        {
            encoded[index] = Alphabet[ReadBits(bytes, index * 5, 5)];
        }

        return string.Create(
            24,
            encoded.ToString(),
            static (destination, source) =>
            {
                "SWD1-".AsSpan().CopyTo(destination);
                source.AsSpan(0, 4).CopyTo(destination[5..]);
                destination[9] = '-';
                source.AsSpan(4, 4).CopyTo(destination[10..]);
                destination[14] = '-';
                source.AsSpan(8, 4).CopyTo(destination[15..]);
                destination[19] = '-';
                source.AsSpan(12, 4).CopyTo(destination[20..]);
            });
    }

    public static bool TryDecode(string? value, out Swd1Payload? payload)
    {
        payload = null;
        if (!TryNormalize(value, out var encoded))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[EncodedByteCount];
        bytes.Clear();
        for (var index = 0; index < EncodedCharacterCount; index++)
        {
            var decoded = DecodeCharacter(encoded[index]);
            if (decoded < 0)
            {
                return false;
            }

            WriteBits(bytes, index * 5, 5, decoded);
        }

        if (bytes[PayloadByteCount] != ComputeCrc8(bytes[..PayloadByteCount]))
        {
            return false;
        }

        try
        {
            var reader = new Swd1BitReader(bytes[..PayloadByteCount]);
            var component = (Swd1Component)reader.Read(1);
            var common = ReadCommon(ref reader, component);
            payload = component switch
            {
                Swd1Component.AgentSetup =>
                    Swd1Payload.ForAgent(common, ReadAgentTail(ref reader)),
                Swd1Component.Viewer =>
                    Swd1Payload.ForViewer(common, ReadViewerTail(ref reader)),
                _ => null
            };
            return payload is not null &&
                   reader.BitsRead == PayloadByteCount * 8;
        }
        catch (ArgumentException)
        {
            payload = null;
            return false;
        }
    }

    private static void WriteCommon(
        ref Swd1BitWriter writer,
        Swd1CommonFields common)
    {
        writer.Write((uint)common.Component, 1);
        writer.Write(common.ProductVersion.Major, 4);
        writer.Write(common.ProductVersion.Minor, 8);
        writer.Write(common.ProductVersion.Patch, 8);
        writer.Write(common.OperationCode, 2);
        writer.Write(common.ResultCode, 6);
        writer.Write(common.PrimaryCode, 6);
    }

    private static Swd1CommonFields ReadCommon(
        ref Swd1BitReader reader,
        Swd1Component component)
    {
        var version = Swd1SemanticVersion.FromEncoded(
            (byte)reader.Read(4),
            (byte)reader.Read(8),
            (byte)reader.Read(8));
        return new Swd1CommonFields(
            component,
            version,
            (byte)reader.Read(2),
            (byte)reader.Read(6),
            (byte)reader.Read(6));
    }

    private static void WriteAgentTail(
        ref Swd1BitWriter writer,
        Swd1AgentTail tail)
    {
        writer.Write((uint)tail.RollbackFlags, 13);
        writer.Write((uint)tail.JournalState, 2);
        writer.Write((uint)tail.ServiceState, 3);
        writer.Write((uint)tail.LocalTcp18443, 2);
        writer.Write((uint)tail.Readiness, 2);
        writer.Write((uint)tail.PackageValidation, 2);
        writer.Write((uint)tail.FirewallFlags, 9);
        writer.Write(tail.Reserved, 4);
    }

    private static Swd1AgentTail ReadAgentTail(ref Swd1BitReader reader) =>
        new(
            (Swd1AgentRollbackFlags)reader.Read(13),
            (Swd1AgentJournalState)reader.Read(2),
            (Swd1AgentServiceState)reader.Read(3),
            (Swd1CheckState)reader.Read(2),
            (Swd1CheckState)reader.Read(2),
            (Swd1CheckState)reader.Read(2),
            (Swd1AgentFirewallFlags)reader.Read(9),
            (byte)reader.Read(4));

    private static void WriteViewerTail(
        ref Swd1BitWriter writer,
        Swd1ViewerTail tail)
    {
        writer.Write((uint)tail.Mode, 1);
        writer.Write((uint)tail.FailedStage, 3);
        writer.Write((uint)tail.Stages.Address, 2);
        writer.Write((uint)tail.Stages.Dns, 2);
        writer.Write((uint)tail.Stages.Tcp, 2);
        writer.Write((uint)tail.Stages.Https, 2);
        writer.Write((uint)tail.Stages.Identity, 2);
        writer.Write(tail.CandidateCountCode, 4);
        writer.Write(tail.AgentVersion.Major, 4);
        writer.Write(tail.AgentVersion.Minor, 6);
        writer.Write(tail.AgentVersion.Patch, 6);
        writer.Write(tail.ApiVersionCode, 3);
    }

    private static Swd1ViewerTail ReadViewerTail(ref Swd1BitReader reader)
    {
        var mode = (Swd1ViewerMode)reader.Read(1);
        var failedStage = (Swd1ViewerFailedStage)reader.Read(3);
        var stages = new Swd1ViewerStageStates(
            (Swd1ViewerStageState)reader.Read(2),
            (Swd1ViewerStageState)reader.Read(2),
            (Swd1ViewerStageState)reader.Read(2),
            (Swd1ViewerStageState)reader.Read(2),
            (Swd1ViewerStageState)reader.Read(2));
        var candidate = (byte)reader.Read(4);
        var version = Swd1CompactSemanticVersion.FromEncoded(
            (byte)reader.Read(4),
            (byte)reader.Read(6),
            (byte)reader.Read(6));
        var api = (byte)reader.Read(3);
        return new Swd1ViewerTail(
            mode,
            failedStage,
            stages,
            candidate,
            version,
            api);
    }

    private static void Validate(Swd1Payload payload)
    {
        if (!Enum.IsDefined(payload.Common.Component) ||
            payload.Common.OperationCode > 0x03 ||
            payload.Common.ResultCode > 0x3F ||
            payload.Common.PrimaryCode > 0x3F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                "SWD1 common fields exceed their allocated bit widths.");
        }

        switch (payload.Common.Component)
        {
            case Swd1Component.AgentSetup
                when payload.Agent is { } agent && payload.Viewer is null:
                ValidateAgent(agent);
                break;
            case Swd1Component.Viewer
                when payload.Viewer is { } viewer && payload.Agent is null:
                ValidateViewer(viewer);
                break;
            default:
                throw new ArgumentException(
                    "SWD1 component and tail do not match.",
                    nameof(payload));
        }
    }

    private static void ValidateAgent(Swd1AgentTail tail)
    {
        if (((ushort)tail.RollbackFlags & ~0x1FFF) != 0 ||
            (byte)tail.JournalState > 0x03 ||
            (byte)tail.ServiceState > 0x07 ||
            (byte)tail.LocalTcp18443 > 0x03 ||
            (byte)tail.Readiness > 0x03 ||
            (byte)tail.PackageValidation > 0x03 ||
            ((ushort)tail.FirewallFlags & ~0x01FF) != 0 ||
            tail.Reserved > 0x0F)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tail),
                "SWD1 Agent tail exceeds its allocated bit widths.");
        }
    }

    private static void ValidateViewer(Swd1ViewerTail tail)
    {
        if ((byte)tail.Mode > 0x01 ||
            (byte)tail.FailedStage > 0x07 ||
            (byte)tail.Stages.Address > 0x03 ||
            (byte)tail.Stages.Dns > 0x03 ||
            (byte)tail.Stages.Tcp > 0x03 ||
            (byte)tail.Stages.Https > 0x03 ||
            (byte)tail.Stages.Identity > 0x03 ||
            tail.CandidateCountCode > 0x0F ||
            tail.ApiVersionCode > 0x07)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tail),
                "SWD1 Viewer tail exceeds its allocated bit widths.");
        }
    }

    private static bool TryNormalize(string? value, out string encoded)
    {
        encoded = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<char> normalized = stackalloc char[Prefix.Length + EncodedCharacterCount];
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character == '-')
            {
                continue;
            }

            if (count == normalized.Length)
            {
                return false;
            }

            normalized[count++] = char.ToUpperInvariant(character);
        }

        if (count != normalized.Length ||
            !normalized[..Prefix.Length].SequenceEqual(Prefix))
        {
            return false;
        }

        encoded = normalized[Prefix.Length..].ToString();
        return true;
    }

    private static int DecodeCharacter(char character)
    {
        var normalized = character switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            _ => character
        };
        return Alphabet.IndexOf(normalized);
    }

    private static byte ComputeCrc8(ReadOnlySpan<byte> payload)
    {
        byte crc = 0;
        foreach (var value in payload)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc << 1);
            }
        }

        return crc;
    }

    private static int ReadBits(
        ReadOnlySpan<byte> bytes,
        int bitOffset,
        int bitCount)
    {
        var result = 0;
        for (var bit = 0; bit < bitCount; bit++)
        {
            var absolute = bitOffset + bit;
            result = (result << 1) |
                     ((bytes[absolute / 8] >> (7 - absolute % 8)) & 1);
        }

        return result;
    }

    private static void WriteBits(
        Span<byte> bytes,
        int bitOffset,
        int bitCount,
        int value)
    {
        for (var bit = 0; bit < bitCount; bit++)
        {
            var absolute = bitOffset + bit;
            var source = (value >> (bitCount - bit - 1)) & 1;
            if (source != 0)
            {
                bytes[absolute / 8] |= (byte)(1 << (7 - absolute % 8));
            }
        }
    }
}

internal static class Swd1MappingTables
{
    internal const byte UnknownDiagnosticCode = 0x3F;
    internal const byte ViewerCandidateUnknown = 0x0F;
    internal const byte ViewerApiUnknown = 0x07;

    // Numeric positions are protocol values. Append only; never reorder.
    private static readonly string[] AgentDiagnosticCodes =
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

    // Numeric positions are protocol values. Append only; never reorder.
    private static readonly string[] ViewerDiagnosticCodes =
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

    private static readonly IReadOnlyDictionary<string, Swd1AgentRollbackFlags>
        RollbackCodeFlags =
            new Dictionary<string, Swd1AgentRollbackFlags>(StringComparer.Ordinal)
            {
                ["ROLLBACK_STATE_MISMATCH"] = Swd1AgentRollbackFlags.StateMismatch,
                ["ROLLBACK_SERVICE_STOP_FAILED"] = Swd1AgentRollbackFlags.ServiceStop,
                ["ROLLBACK_FILE_RESTORE_FAILED"] = Swd1AgentRollbackFlags.FileRestore,
                ["ROLLBACK_DATA_CLEANUP_FAILED"] = Swd1AgentRollbackFlags.DataCleanup,
                ["ROLLBACK_SERVICE_RESTORE_FAILED"] = Swd1AgentRollbackFlags.ServiceRestore,
                ["ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED"] =
                    Swd1AgentRollbackFlags.HttpsFirewallRestore,
                ["ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED"] =
                    Swd1AgentRollbackFlags.LegacyFirewallRestore,
                ["ROLLBACK_JOURNAL_WRITE_FAILED"] = Swd1AgentRollbackFlags.JournalWrite,
                ["ROLLBACK_EVIDENCE_CLEANUP_FAILED"] =
                    Swd1AgentRollbackFlags.EvidenceCleanup,
                ["ROLLBACK_STAGING_CLEANUP_FAILED"] =
                    Swd1AgentRollbackFlags.StagingCleanup,
                ["ROLLBACK_BACKUP_CLEANUP_FAILED"] =
                    Swd1AgentRollbackFlags.BackupCleanup,
                ["ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED"] =
                    Swd1AgentRollbackFlags.FailedDirectoryCleanup,
                ["ROLLBACK_JOURNAL_CLEANUP_FAILED"] =
                    Swd1AgentRollbackFlags.JournalCleanup
            };

    private static readonly IReadOnlyDictionary<string, Swd1AgentFirewallFlags>
        FirewallCodeFlags =
            new Dictionary<string, Swd1AgentFirewallFlags>(StringComparer.Ordinal)
            {
                ["FIREWALL_RULE_MISSING"] = Swd1AgentFirewallFlags.Missing,
                ["FIREWALL_RULE_DISABLED"] = Swd1AgentFirewallFlags.Disabled,
                ["FIREWALL_DIRECTION_MISMATCH"] =
                    Swd1AgentFirewallFlags.Direction,
                ["FIREWALL_ACTION_MISMATCH"] = Swd1AgentFirewallFlags.Action,
                ["FIREWALL_PROTOCOL_MISMATCH"] =
                    Swd1AgentFirewallFlags.Protocol,
                ["FIREWALL_PORT_MISMATCH"] = Swd1AgentFirewallFlags.Port,
                ["FIREWALL_REMOTE_ADDRESS_MISMATCH"] =
                    Swd1AgentFirewallFlags.RemoteAddress,
                ["FIREWALL_PROFILE_MISMATCH"] =
                    Swd1AgentFirewallFlags.Profiles,
                ["FIREWALL_EDGE_TRAVERSAL_MISMATCH"] =
                    Swd1AgentFirewallFlags.EdgeTraversal
            };

    internal static byte AgentOperationCode(string? value) => value switch
    {
        "preflight" or "PREFLIGHT" => 1,
        "install" or "INSTALL" => 2,
        "recovery" or "RECOVERY" => 3,
        _ => 0
    };

    internal static byte ViewerOperationCode(string? value) =>
        value is "AGENT_CONNECTION_CHECK" or "agent_connection_check"
            ? (byte)1
            : (byte)0;

    internal static string OperationName(
        Swd1Component component,
        byte code) =>
        component switch
        {
            Swd1Component.AgentSetup => code switch
            {
                1 => "PREFLIGHT",
                2 => "INSTALL",
                3 => "RECOVERY",
                _ => "UNKNOWN"
            },
            Swd1Component.Viewer when code == 1 => "AGENT_CONNECTION_CHECK",
            _ => "UNKNOWN"
        };

    internal static byte DiagnosticCode(
        Swd1Component component,
        string? value)
    {
        if (value is null)
        {
            return UnknownDiagnosticCode;
        }

        var table = component == Swd1Component.AgentSetup
            ? AgentDiagnosticCodes
            : ViewerDiagnosticCodes;
        var index = Array.IndexOf(table, value);
        return index >= 0 ? (byte)index : UnknownDiagnosticCode;
    }

    internal static string DiagnosticCodeName(
        Swd1Component component,
        byte value)
    {
        var table = component == Swd1Component.AgentSetup
            ? AgentDiagnosticCodes
            : ViewerDiagnosticCodes;
        return value < table.Length ? table[value] : "UNKNOWN";
    }

    internal static Swd1AgentRollbackFlags RollbackFlags(
        IEnumerable<string>? values)
    {
        var result = Swd1AgentRollbackFlags.None;
        foreach (var value in values ?? [])
        {
            if (RollbackCodeFlags.TryGetValue(value, out var flag))
            {
                result |= flag;
            }
        }

        return result;
    }

    internal static Swd1AgentJournalState AgentJournalState(
        string? value) => value switch
        {
            "NONE" => Swd1AgentJournalState.None,
            "PENDING_RECOVERABLE" => Swd1AgentJournalState.PendingRecoverable,
            "PENDING_BLOCKED" => Swd1AgentJournalState.PendingBlocked,
            _ => Swd1AgentJournalState.Unknown
        };

    internal static Swd1AgentServiceState AgentServiceState(
        string? value) => value switch
        {
            "NOT_INSTALLED" => Swd1AgentServiceState.NotInstalled,
            "FOUND" => Swd1AgentServiceState.Found,
            "CONFIGURED" => Swd1AgentServiceState.Configured,
            "RUNNING" => Swd1AgentServiceState.Running,
            "RUNNING_READY" => Swd1AgentServiceState.RunningReady,
            "STOPPED" => Swd1AgentServiceState.Stopped,
            "FAIL" => Swd1AgentServiceState.Failed,
            _ => Swd1AgentServiceState.Unknown
        };

    internal static Swd1CheckState LocalTcpState(string? value) =>
        value switch
        {
            "NOT_RUN" => Swd1CheckState.NotRun,
            "PASS" => Swd1CheckState.Passed,
            "NOT_CONFIRMED" => Swd1CheckState.FailedOrNotConfirmed,
            _ => Swd1CheckState.Unknown
        };

    internal static Swd1CheckState ReadinessState(string? value) =>
        value switch
        {
            "NOT_RUN" => Swd1CheckState.NotRun,
            "PASS" => Swd1CheckState.Passed,
            "FAIL" => Swd1CheckState.FailedOrNotConfirmed,
            _ => Swd1CheckState.Unknown
        };

    internal static Swd1CheckState PackageState(string? value) =>
        value switch
        {
            "NOT_RUN" => Swd1CheckState.NotRun,
            "PASS" => Swd1CheckState.Passed,
            "FAIL" => Swd1CheckState.FailedOrNotConfirmed,
            _ => Swd1CheckState.Unknown
        };

    internal static Swd1AgentFirewallFlags FirewallFlags(
        IEnumerable<string>? values)
    {
        var result = Swd1AgentFirewallFlags.None;
        foreach (var value in values ?? [])
        {
            if (FirewallCodeFlags.TryGetValue(value, out var flag))
            {
                result |= flag;
            }
        }

        return result;
    }

    internal static Swd1ViewerMode ViewerMode(string? value) =>
        value == "SAME_PC"
            ? Swd1ViewerMode.SamePc
            : Swd1ViewerMode.Normal;

    internal static Swd1ViewerFailedStage ViewerFailedStage(
        string? value) => value switch
        {
            "NONE" => Swd1ViewerFailedStage.None,
            "ADDRESS" => Swd1ViewerFailedStage.Address,
            "DNS" => Swd1ViewerFailedStage.Dns,
            "TCP" => Swd1ViewerFailedStage.Tcp,
            "HTTPS" => Swd1ViewerFailedStage.Https,
            "IDENTITY" => Swd1ViewerFailedStage.Identity,
            "SETTINGS" => Swd1ViewerFailedStage.Settings,
            _ => Swd1ViewerFailedStage.Unknown
        };

    internal static Swd1ViewerStageState ViewerStageState(
        string? value) => value switch
        {
            "RUNNING" => Swd1ViewerStageState.Running,
            "SUCCEEDED" => Swd1ViewerStageState.Succeeded,
            "FAILED" => Swd1ViewerStageState.Failed,
            _ => Swd1ViewerStageState.NotRun
        };

    internal static byte ViewerCandidateCode(int value) =>
        value is >= 0 and < ViewerCandidateUnknown
            ? (byte)value
            : ViewerCandidateUnknown;

    internal static byte ViewerApiCode(string? value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed is >= 0 and < ViewerApiUnknown
            ? (byte)parsed
            : ViewerApiUnknown;
}

internal static class Swd1VersionParser
{
    internal static bool TryParse(
        string? value,
        out int major,
        out int minor,
        out int patch)
    {
        major = 0;
        minor = 0;
        patch = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        var parts = normalized.Split('.');
        return parts.Length == 3 &&
               int.TryParse(
                   parts[0],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out major) &&
               int.TryParse(
                   parts[1],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out minor) &&
               int.TryParse(
                   parts[2],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out patch);
    }
}

internal ref struct Swd1BitWriter
{
    private readonly Span<byte> _destination;

    internal Swd1BitWriter(Span<byte> destination)
    {
        _destination = destination;
        BitsWritten = 0;
    }

    internal int BitsWritten { get; private set; }

    internal void Write(uint value, int bitCount)
    {
        if (bitCount is < 1 or > 32 ||
            BitsWritten + bitCount > _destination.Length * 8 ||
            (bitCount < 32 && value >= (1u << bitCount)))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        for (var bit = bitCount - 1; bit >= 0; bit--)
        {
            if (((value >> bit) & 1u) != 0)
            {
                var destinationBit = BitsWritten;
                _destination[destinationBit / 8] |=
                    (byte)(1 << (7 - destinationBit % 8));
            }

            BitsWritten++;
        }
    }
}

internal ref struct Swd1BitReader
{
    private readonly ReadOnlySpan<byte> _source;

    internal Swd1BitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        BitsRead = 0;
    }

    internal int BitsRead { get; private set; }

    internal uint Read(int bitCount)
    {
        if (bitCount is < 1 or > 32 ||
            BitsRead + bitCount > _source.Length * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        uint value = 0;
        for (var bit = 0; bit < bitCount; bit++)
        {
            var sourceBit = BitsRead++;
            value = (value << 1) |
                    (uint)((_source[sourceBit / 8] >>
                            (7 - sourceBit % 8)) & 1);
        }

        return value;
    }
}
