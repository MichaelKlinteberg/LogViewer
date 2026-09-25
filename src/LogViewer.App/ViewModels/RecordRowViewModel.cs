using System.Windows.Media;
using LogViewer.Core.Records;

namespace LogViewer.App.ViewModels;

/// <summary>Immutable UI-ready projection of one <see cref="LogRecord"/> for display in the row list.</summary>
public sealed class RecordRowViewModel
{
    public required long Index { get; init; }

    public required string RawText { get; init; }

    public string TimestampText { get; init; } = string.Empty;

    public string Component { get; init; } = string.Empty;

    public string SeverityText { get; init; } = string.Empty;

    public string Thread { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsHighlighted { get; init; }

    public Brush RowBackground { get; init; } = Brushes.Transparent;

    public Brush RowForeground { get; init; } = Brushes.Black;

    public static RecordRowViewModel Create(LogRecord record, bool highlighted)
    {
        var cm = record.CmTrace;
        return new RecordRowViewModel
        {
            Index = record.Index,
            RawText = record.RawText,
            TimestampText = cm?.Timestamp?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? string.Empty,
            Component = cm?.Component ?? string.Empty,
            SeverityText = cm?.Severity.ToString() ?? string.Empty,
            Thread = cm?.Thread ?? string.Empty,
            Message = cm?.Message ?? record.RawText,
            IsHighlighted = highlighted,
            RowBackground = highlighted ? Brushes.Khaki : Brushes.Transparent,
            RowForeground = cm?.Severity switch
            {
                LogSeverity.Error => Brushes.Firebrick,
                LogSeverity.Warning => Brushes.DarkGoldenrod,
                _ => Brushes.Black,
            },
        };
    }
}
