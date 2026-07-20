using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public enum ViewerExportFormat
{
    Csv,
    Json
}

public sealed record ViewerExportResult(bool Success, string Code, int ExportedCount);

public sealed class ViewerExportService
{
    private static readonly UTF8Encoding CsvEncoding = new(true);
    private static readonly UTF8Encoding JsonEncoding = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ViewerExportResult> ExportAsync(
        string path,
        ViewerExportFormat format,
        IEnumerable<EventViewModel> events,
        CancellationToken cancellationToken = default)
    {
        var items = events.Select(SanitizedExportEvent.From).ToArray();
        if (items.Length == 0) return new ViewerExportResult(false, "EXPORT_NO_EVENTS", 0);
        if (!TryResolvePath(path, format, out var fullPath))
        {
            return new ViewerExportResult(false, "EXPORT_PATH_INVALID", 0);
        }

        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var content = format == ViewerExportFormat.Csv
                ? BuildCsv(items)
                : BuildJson(items);
            var encoding = format == ViewerExportFormat.Csv ? CsvEncoding : JsonEncoding;
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, encoding))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporaryPath, fullPath, true);
            return new ViewerExportResult(true, "EXPORT_OK", items.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ViewerExportResult(false, "EXPORT_CANCELLED", 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new ViewerExportResult(false, "EXPORT_WRITE_DENIED", 0);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or PathTooLongException)
        {
            return new ViewerExportResult(false, "EXPORT_WRITE_FAILED", 0);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    internal static string BuildCsv(IReadOnlyList<SanitizedExportEvent> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("occurred_local,severity,status,event_type,device_alias,title,detail");
        foreach (var item in items)
        {
            builder.Append(Csv(item.OccurredLocal)).Append(',')
                .Append(Csv(item.Severity)).Append(',')
                .Append(Csv(item.Status)).Append(',')
                .Append(Csv(item.EventType)).Append(',')
                .Append(Csv(item.DeviceAlias)).Append(',')
                .Append(Csv(item.Title)).Append(',')
                .Append(Csv(item.Detail)).AppendLine();
        }
        return builder.ToString();
    }

    internal static string BuildJson(IReadOnlyList<SanitizedExportEvent> items) => JsonSerializer.Serialize(new
    {
        schema = "samsung-switch-watch/sanitized-events/v1",
        exportedAtUtc = DateTimeOffset.UtcNow,
        rawOutputIncluded = false,
        events = items
    }, JsonOptions);

    private static string Csv(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static bool TryResolvePath(string? path, ViewerExportFormat format, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000) return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            var expected = format == ViewerExportFormat.Csv ? ".csv" : ".json";
            var directory = Path.GetDirectoryName(fullPath);
            return Path.IsPathFullyQualified(fullPath)
                   && string.Equals(Path.GetExtension(fullPath), expected, StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(directory)
                   && Directory.Exists(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed record SanitizedExportEvent(
    string OccurredLocal,
    string Severity,
    string Status,
    string EventType,
    string DeviceAlias,
    string Title,
    string Detail)
{
    public static SanitizedExportEvent From(EventViewModel item) => new(
        item.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        item.Severity.ToString(),
        item.StatusText,
        DiagnosticTextSanitizer.Clean(item.Kind),
        DiagnosticTextSanitizer.DeviceAlias(item.DeviceId),
        DiagnosticTextSanitizer.Clean(item.Title),
        DiagnosticTextSanitizer.Clean(item.AlertDetail));
}

internal static partial class DiagnosticTextSanitizer
{
    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();
    [GeneratedRegex(@"(?i)(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();
    [GeneratedRegex(@"(?i)\b(?:bearer|token|password|community)\s*[:=]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();
    [GeneratedRegex(@"(?i)\b(?:host(?:name)?|user(?:name)?|login)\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedIdentity();
    [GeneratedRegex(@"(?i)\b(?:[a-z0-9](?:[a-z0-9-]{0,62})\.)+(?:local|lan|corp|internal|com|net|org|co\.kr)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
    [GeneratedRegex(@"(?i)\b(?:https?|telnet)://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex UriValue();
    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();

    public static string Clean(string? value)
    {
        var clean = ControlCharacters().Replace(value ?? string.Empty, string.Empty);
        clean = Secret().Replace(clean, "[REDACTED]");
        clean = NamedIdentity().Replace(clean, "[REDACTED_IDENTITY]");
        clean = UriValue().Replace(clean, "[REDACTED_URI]");
        clean = HostName().Replace(clean, "[REDACTED_HOST]");
        clean = WindowsPath().Replace(clean, "[REDACTED_PATH]");
        clean = Mac().Replace(clean, "[REDACTED_MAC]");
        clean = Ipv4().Replace(clean, "[REDACTED_IP]");
        return clean.Length <= 512 ? clean : clean[..512] + "…";
    }

    public static string DeviceAlias(string? deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId ?? string.Empty));
        return "DEVICE-" + Convert.ToHexString(hash.AsSpan(0, 4));
    }
}
