using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Diff;

public sealed record LogCursor(
    bool IsInitialized,
    IReadOnlyList<string> EntryIdentities,
    bool AwaitingNonEmptyBaseline = false)
{
    public static LogCursor Empty { get; } = new(false, []);
}

public sealed record LogComparison(
    LogCursor Cursor,
    IReadOnlyList<SwitchLogEntry> NewEntries,
    bool WasBaselined,
    bool BufferWasReset);

public static class LogCursorEngine
{
    public static LogComparison Compare(LogCursor? previous, LogSnapshot current, bool forceBaseline = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        previous ??= LogCursor.Empty;
        var currentIds = current.Entries.Select(static entry => entry.Identity).Distinct(StringComparer.Ordinal).ToArray();
        var next = new LogCursor(true, currentIds);

        if (!previous.IsInitialized || forceBaseline || (previous.AwaitingNonEmptyBaseline && currentIds.Length > 0))
        {
            return new LogComparison(next, [], true, false);
        }

        var previousIds = previous.EntryIdentities.ToHashSet(StringComparer.Ordinal);
        if (previousIds.Count == 0)
        {
            return new LogComparison(next, current.Entries, false, false);
        }

        if (currentIds.Length == 0)
        {
            return new LogComparison(next with { AwaitingNonEmptyBaseline = true }, [], false, true);
        }

        var hasOverlap = currentIds.Any(previousIds.Contains);
        if (!hasOverlap)
        {
            // The RAM buffer either wrapped, was cleared, or was replaced. It
            // is safer to establish a new baseline than to alert on the entire
            // buffer as if every historical line were new.
            return new LogComparison(next, [], true, true);
        }

        var additions = current.Entries.Where(entry => !previousIds.Contains(entry.Identity)).ToArray();
        return new LogComparison(next, additions, false, false);
    }
}
