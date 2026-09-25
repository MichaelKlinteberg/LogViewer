using LogViewer.Core.Records;

namespace LogViewer.Core.Filtering;

/// <summary>
/// The three independent, per-tab text filters. Matching is always a simple case-insensitive
/// substring match (ordinal, culture independent) - no regex. An empty/whitespace string disables
/// that particular filter. Combination order, applied to the record's raw text (never the
/// parsed/derived display text, so results are identical whether the tab is showing the raw or the
/// parsed CMTrace view): Filter, then Hide, then Highlight.
/// </summary>
public sealed class FilterState
{
    public string Highlight { get; init; } = string.Empty;
    public string Hide { get; init; } = string.Empty;
    public string Filter { get; init; } = string.Empty;

    public bool HasHighlight => !string.IsNullOrWhiteSpace(Highlight);
    public bool HasHide => !string.IsNullOrWhiteSpace(Hide);
    public bool HasFilter => !string.IsNullOrWhiteSpace(Filter);
}

/// <summary>Outcome of evaluating a record against the current <see cref="FilterState"/>.</summary>
public readonly record struct FilterOutcome(bool Visible, bool Highlighted);

public static class FilterEngine
{
    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates visibility and highlight state for a record. Filter and Hide are combined with AND
    /// logic (a record must match Filter, when set, AND must NOT match Hide, when set) to determine
    /// visibility; Highlight never affects visibility, only presentation.
    /// </summary>
    public static FilterOutcome Evaluate(LogRecord record, FilterState state)
    {
        var text = record.RawText;

        bool passesFilter = !state.HasFilter || ContainsIgnoreCase(text, state.Filter);
        bool passesHide = !state.HasHide || !ContainsIgnoreCase(text, state.Hide);
        bool visible = passesFilter && passesHide;

        bool highlighted = visible && state.HasHighlight && ContainsIgnoreCase(text, state.Highlight);

        return new FilterOutcome(visible, highlighted);
    }
}
