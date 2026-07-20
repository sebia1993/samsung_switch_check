using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Parsing;

public static partial class LogOutputParser
{
    public static ParseResult<LogSnapshot> Parse(string output)
    {
        var normalized = Telnet.OutputNormalizer.CleanControlCharacters(output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (string.IsNullOrWhiteSpace(normalized) || EmptyLogRegex().IsMatch(normalized.Trim()))
        {
            return ParseResult<LogSnapshot>.Success(new LogSnapshot([]));
        }

        var lines = normalized.Split('\n');
        var entries = new List<SwitchLogEntry>();
        LogBuilder? current = null;
        var sawHeader = false;
        var incompleteEntry = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var header = BracketHeaderRegex().Match(line);
            if (header.Success)
            {
                sawHeader = true;
                if (current is not null)
                {
                    incompleteEntry |= !TryAddEntry(entries, current);
                }

                current = new LogBuilder
                {
                    Sequence = ParseNullableInt(header.Groups["sequence"].Value),
                    Timestamp = ParseTimestamp(header.Groups["date"].Value, header.Groups["time"].Value)
                };
                continue;
            }

            var singleLine = SingleLineRegex().Match(line);
            if (singleLine.Success)
            {
                sawHeader = true;
                if (current is not null)
                {
                    incompleteEntry |= !TryAddEntry(entries, current);
                    current = null;
                }

                var builder = new LogBuilder
                {
                    Sequence = ParseNullableInt(singleLine.Groups["sequence"].Value),
                    Timestamp = ParseTimestamp(singleLine.Groups["date"].Value, singleLine.Groups["time"].Value)
                };
                builder.MessageLines.Add(singleLine.Groups["message"].Value.Trim());
                incompleteEntry |= !TryAddEntry(entries, builder);
                continue;
            }

            if (current is null || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var metadata = MetadataRegex().Match(line);
            if (metadata.Success)
            {
                current.Level = ParseNullableInt(metadata.Groups["level"].Value);
                current.Module = ParseNullableInt(metadata.Groups["module"].Value);
                current.Function = ParseNullableInt(metadata.Groups["function"].Value);
                current.EventNumber = ParseNullableInt(metadata.Groups["event"].Value);
            }
            else
            {
                current.MessageLines.Add(line.Trim('"'));
            }
        }

        if (current is not null)
        {
            incompleteEntry |= !TryAddEntry(entries, current);
        }

        if (incompleteEntry)
        {
            return ParseResult<LogSnapshot>.Failure(
                ErrorCodes.IncompleteOutput,
                "parse-log-ram",
                "The RAM log ended with an incomplete entry.",
                true);
        }

        if (entries.Count == 0)
        {
            return sawHeader
                ? ParseResult<LogSnapshot>.Failure(
                    ErrorCodes.IncompleteOutput,
                    "parse-log-ram",
                    "The RAM log contained headers but no complete entries.",
                    true)
                : ParseResult<LogSnapshot>.Unsupported(
                    "parse-log-ram",
                    "The RAM log format is not recognized for this firmware.");
        }

        return ParseResult<LogSnapshot>.Success(new LogSnapshot(entries));
    }

    private static bool TryAddEntry(ICollection<SwitchLogEntry> entries, LogBuilder builder)
    {
        var message = string.Join(" ", builder.MessageLines)
            .Trim()
            .Trim('"')
            .Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var canonical = string.Join('|',
            builder.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            builder.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
            message,
            builder.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            builder.Module?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            builder.Function?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            builder.EventNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        entries.Add(new SwitchLogEntry(
            identity,
            builder.Sequence,
            builder.Timestamp,
            message,
            builder.Level,
            builder.Module,
            builder.Function,
            builder.EventNumber));
        return true;
    }

    private static DateTime? ParseTimestamp(string date, string time)
    {
        var combined = $"{date} {time}";
        return DateTime.TryParseExact(
            combined,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : null;
    }

    private static int? ParseNullableInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class LogBuilder
    {
        public int? Sequence { get; init; }

        public DateTime? Timestamp { get; init; }

        public int? Level { get; set; }

        public int? Module { get; set; }

        public int? Function { get; set; }

        public int? EventNumber { get; set; }

        public List<string> MessageLines { get; } = [];
    }

    [GeneratedRegex(@"^\s*\[(?<sequence>\d+)\]\s+(?:(?<time>\d{2}:\d{2}:\d{2})\s+(?<date>\d{4}-\d{2}-\d{2})|(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}))", RegexOptions.CultureInvariant)]
    private static partial Regex BracketHeaderRegex();

    [GeneratedRegex(@"^\s*(?<sequence>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<message>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLineRegex();

    [GeneratedRegex(@"(?i)level\s*:\s*(?<level>\d+).*?module\s*:\s*(?<module>\d+).*?function\s*:\s*(?<function>\d+).*?event\s*(?:no\.?|number)?\s*:\s*(?<event>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataRegex();

    [GeneratedRegex(@"(?is)^\s*(?:no\s+(?:log(?:\s+(?:entries|messages))?|entries|messages)|log\s+(?:is\s+)?empty)\s*[.!]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyLogRegex();
}
