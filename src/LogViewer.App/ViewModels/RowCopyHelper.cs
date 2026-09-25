namespace LogViewer.App.ViewModels;

/// <summary>
/// Builds the clipboard text for a "Copy" action on selected rows. Kept as a small, pure, testable
/// piece separate from the WPF selection plumbing in <see cref="Views.LogTabView"/>.
/// </summary>
public static class RowCopyHelper
{
    /// <summary>
    /// Joins the original raw text of the given rows, ordered by their file position (not selection
    /// order, which WPF reports in click order), one row per line, using the given <paramref name="newLine"/>.
    /// Always uses <see cref="RecordRowViewModel.RawText"/> — the original source-file content — regardless
    /// of whether the caller is currently displaying the raw or parsed-columns view, so a copy always
    /// reproduces exactly what is in the log file for the selected rows (including full multi-line
    /// CMTrace records, since <c>RawText</c> holds the complete record text).
    /// </summary>
    public static string BuildClipboardText(IEnumerable<RecordRowViewModel> selectedRows, string? newLine = null)
    {
        ArgumentNullException.ThrowIfNull(selectedRows);
        newLine ??= Environment.NewLine;

        var ordered = selectedRows
            .OrderBy(r => r.Index)
            .Select(r => r.RawText);

        return string.Join(newLine, ordered);
    }
}
