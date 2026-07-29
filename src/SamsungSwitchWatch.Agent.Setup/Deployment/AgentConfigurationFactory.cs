using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public static class AgentConfigurationFactory
{
    public static string Create(
        string dataDirectory,
        IReadOnlyList<string> allowedTargetCidrs,
        string allowedViewerIpv4,
        string? existingConfiguration)
    {
        var existing = ReadExistingAgent(existingConfiguration);
        var agentId = ReadString(existing, "AgentId", IsAgentId) ??
                      SanitizeAgentId($"agent-{Environment.MachineName}");
        var maxConcurrent = ReadInt(existing, "MaxConcurrentExecutions", 1, 16, 2);
        var rateLimit = ReadInt(existing, "RateLimitPerMinute", 1, 120, 60);
        var maxRequestBody = ReadInt(existing, "MaxRequestBodyBytes", 4096, 65536, 32768);
        var maxCommands = ReadInt(existing, "MaxCommandsPerRequest", 1, 8, 8);
        var maxCommandLength = ReadInt(existing, "MaxCommandLength", 16, 128, 128);
        var maxOutputBytes = ReadInt(existing, "MaxOutputBytes", 1024, 65536, 65536);
        var existingTelnet = existing?["Telnet"] as JsonObject;
        var maxSessionSeconds = ReadInt(existingTelnet, "MaxSessionSeconds", 30, 240, 240);
        var retryCount = ReadInt(
            existingTelnet,
            "ImmediateSessionCloseRetryCount",
            0,
            1,
            1);
        var retryDelay = ReadInt(
            existingTelnet,
            "ImmediateSessionCloseRetryDelaySeconds",
            1,
            10,
            2);
        var model = new
        {
            Agent = new
            {
                AgentId = agentId,
                ListenUrl = "https://0.0.0.0:18443",
                DataDirectory = Path.GetFullPath(dataDirectory),
                MockMode = false,
                AllowedViewerIpv4 = allowedViewerIpv4,
                AllowedTargetCidrs = allowedTargetCidrs,
                MaxConcurrentExecutions = maxConcurrent,
                RateLimitPerMinute = rateLimit,
                MaxRequestBodyBytes = maxRequestBody,
                MaxCommandsPerRequest = maxCommands,
                MaxCommandLength = maxCommandLength,
                MaxOutputBytes = maxOutputBytes,
                Telnet = new
                {
                    MaxSessionSeconds = maxSessionSeconds,
                    ImmediateSessionCloseRetryCount = retryCount,
                    ImmediateSessionCloseRetryDelaySeconds = retryDelay
                }
            },
            Logging = new
            {
                LogLevel = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning"
                }
            },
            AllowedHosts = "*"
        };

        return JsonSerializer.Serialize(model, JsonOptions);
    }

    private static JsonObject? ReadExistingAgent(string? configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(configuration) ||
                JsonNode.Parse(configuration) is not JsonObject root ||
                root["Agent"] is not JsonObject agent)
            {
                throw InvalidExistingConfiguration();
            }

            return agent;
        }
        catch (JsonException exception)
        {
            throw InvalidExistingConfiguration(exception);
        }
    }

    private static SetupException InvalidExistingConfiguration(Exception? innerException = null) =>
        new(
            SetupErrorCodes.ConfigurationInvalid,
            "기존 Agent 설정 파일의 JSON 형식이 올바르지 않아 안전하게 업데이트할 수 없습니다.",
            innerException);

    private static string? ReadString(
        JsonObject? source,
        string propertyName,
        Func<string?, bool> validator)
    {
        try
        {
            var value = source?[propertyName]?.GetValue<string>();
            return validator(value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int ReadInt(
        JsonObject? source,
        string propertyName,
        int minimum,
        int maximum,
        int defaultValue)
    {
        try
        {
            var value = source?[propertyName]?.GetValue<int>();
            return value.HasValue && value.Value >= minimum && value.Value <= maximum
                ? value.Value
                : defaultValue;
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }

    private static string SanitizeAgentId(string candidate)
    {
        var characters = candidate
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-')
            .Take(64)
            .ToArray();
        var result = new string(characters).Trim('-');
        return IsAgentId(result) ? result : "agent-windows";
    }

    private static bool IsAgentId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
