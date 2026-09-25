using LogViewer.App.ViewModels;

namespace LogViewer.App.Tests;

/// <summary>
/// Tests for <see cref="RowCopyHelper"/> — the logic that resolves a set of selected rows to the
/// clipboard text for the "Copy" command. Verifies the row-copy requirements: always copies the
/// underlying raw source-file text (never the parsed/columnized representation), orders by file
/// position rather than selection/click order, and preserves full multi-line record content.
/// </summary>
public class RowCopyHelperTests
{
    private static RecordRowViewModel MakeRow(long index, string rawText, string parsedMessage = "")
    {
        // Constructed directly (rather than via RecordRowViewModel.Create) so a test can simulate a
        // row whose parsed "Message" column differs from its raw text, proving Copy always uses
        // RawText regardless of which columns/view are currently shown.
        return new RecordRowViewModel
        {
            Index = index,
            RawText = rawText,
            Message = parsedMessage,
        };
    }

    [Fact]
    public void BuildClipboardText_joins_raw_text_of_selected_rows_with_newline()
    {
        var rows = new[]
        {
            MakeRow(0, "first raw line"),
            MakeRow(1, "second raw line"),
            MakeRow(2, "third raw line"),
        };

        string result = RowCopyHelper.BuildClipboardText(rows, newLine: "\n");

        Assert.Equal("first raw line\nsecond raw line\nthird raw line", result);
    }

    [Fact]
    public void BuildClipboardText_orders_by_file_position_not_selection_order()
    {
        // WPF reports SelectedItems in click order (e.g. shift-click then ctrl-click can produce any
        // order); Copy must still reproduce the rows in their original file order.
        var rowA = MakeRow(5, "line five");
        var rowB = MakeRow(2, "line two");
        var rowC = MakeRow(9, "line nine");

        string result = RowCopyHelper.BuildClipboardText(new[] { rowA, rowC, rowB }, newLine: "\n");

        Assert.Equal("line two\nline five\nline nine", result);
    }

    [Fact]
    public void BuildClipboardText_uses_raw_text_not_parsed_message_even_when_parsed_view_is_active()
    {
        // The raw record text intentionally differs from the (simulated) parsed Message field; Copy
        // must always take RawText, reproducing exactly what is in the source file.
        var row = MakeRow(0, "raw: full CMTrace line with metadata suffix", parsedMessage: "just the parsed message");

        string result = RowCopyHelper.BuildClipboardText(new[] { row }, newLine: "\n");

        Assert.Equal("raw: full CMTrace line with metadata suffix", result);
    }

    [Fact]
    public void BuildClipboardText_preserves_full_multiline_raw_record_content()
    {
        // If a single "row" corresponds to a multi-line raw CMTrace record, RawText already holds the
        // complete record (embedded newlines and all); Copy must not truncate it to the first line.
        string multilineRaw = "<![LOG[First line of message\r\nSecond line of message]LOG]!><time=\"10:00:00.000+0\" date=\"01-01-2024\" component=\"Comp\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        var row = MakeRow(0, multilineRaw);

        string result = RowCopyHelper.BuildClipboardText(new[] { row }, newLine: "\n");

        Assert.Equal(multilineRaw, result);
    }

    [Fact]
    public void BuildClipboardText_returns_empty_string_for_no_selected_rows()
    {
        string result = RowCopyHelper.BuildClipboardText(Array.Empty<RecordRowViewModel>());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildClipboardText_defaults_newline_to_environment_newline()
    {
        var rows = new[] { MakeRow(0, "a"), MakeRow(1, "b") };

        string result = RowCopyHelper.BuildClipboardText(rows);

        Assert.Equal("a" + Environment.NewLine + "b", result);
    }
}
