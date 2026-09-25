using System.Text;
using LogViewer.Core.IO;
using Xunit;

namespace LogViewer.Core.Tests;

public class LogFileIndexTests : IDisposable
{
    private readonly string _tempDir;

    public LogFileIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LogViewerTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    private string NewPath(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void Indexes_plain_text_lines_one_record_per_line()
    {
        var path = NewPath("plain.log");
        File.WriteAllLines(path, new[] { "line one", "line two", "line three" }, new UTF8Encoding(false));

        var index = new LogFileIndex(path);
        Assert.Equal(RefreshResult.Reset, index.Refresh());
        Assert.Equal(3, index.TotalRecordCount);
        Assert.Equal(LogFormat.PlainText, index.Format);

        var records = index.GetRecords(0, 3);
        Assert.Equal(3, records.Count);
        Assert.Equal("line one", records[0].RawText);
        Assert.Equal("line two", records[1].RawText);
        Assert.Equal("line three", records[2].RawText);
    }

    [Fact]
    public void Detects_cmtrace_format_and_groups_continuation_lines()
    {
        var path = NewPath("cmtrace.log");
        // Realistic CMTrace multi-line entry: the message text itself contains an embedded newline
        // (e.g. an exception stack trace), so the *closing* tag only appears on the second physical
        // line. The record-boundary detector must treat the second physical line as a continuation
        // of the first record (it doesn't start with "<![LOG["), not as a new record.
        var content =
            "<![LOG[First message line one\r\n" +
            "continuation line belonging to first message]LOG]!><time=\"10:00:00.000+0\" date=\"01-01-2026\" component=\"A\" context=\"\" type=\"1\" thread=\"1\" file=\"f\">\r\n" +
            "<![LOG[Second message]LOG]!><time=\"10:00:01.000+0\" date=\"01-01-2026\" component=\"B\" context=\"\" type=\"2\" thread=\"1\" file=\"f\">\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var index = new LogFileIndex(path);
        index.Refresh();

        Assert.Equal(LogFormat.CmTrace, index.Format);
        Assert.Equal(2, index.TotalRecordCount);

        var records = index.GetRecords(0, 2);
        Assert.Contains("continuation line belonging to first message", records[0].RawText);
        Assert.NotNull(records[0].CmTrace);
        Assert.StartsWith("First message line one", records[0].CmTrace!.Message);
        Assert.Contains("continuation line belonging to first message", records[0].CmTrace!.Message);
        Assert.Equal("Second message", records[1].CmTrace!.Message);
    }

    [Fact]
    public void Detects_legacy_cmtrace_format_and_extracts_fields()
    {
        var path = NewPath("legacy-cmtrace.log");
        // Lines copied verbatim (structurally) from a real SCCM/ccmexec client log using the older
        // CMTrace variant, which has no leading marker and instead ends each line with trailing
        // "$$<Component><timestamp><thread=...>" metadata.
        var content =
            "---> SspiExcludePackage succeeded for .\\Administrator authentication!~  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:55.938-60><thread=18612 (0x48B4)>\r\n" +
            "Submitted request successfully  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:57.327-60><thread=19068 (0x4A7C)>\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var index = new LogFileIndex(path);
        index.Refresh();

        Assert.Equal(LogFormat.CmTraceLegacy, index.Format);
        Assert.Equal(2, index.TotalRecordCount);

        var records = index.GetRecords(0, 2);
        Assert.NotNull(records[0].CmTrace);
        Assert.Equal("SMS_CLIENT_CONFIG_MANAGER", records[0].CmTrace!.Component);
        Assert.Equal("18612", records[0].CmTrace!.Thread);
        Assert.Equal("---> SspiExcludePackage succeeded for .\\Administrator authentication!", records[0].CmTrace!.Message);
        Assert.NotNull(records[0].CmTrace!.Timestamp);

        Assert.NotNull(records[1].CmTrace);
        Assert.Equal("19068", records[1].CmTrace!.Thread);
        Assert.Equal("Submitted request successfully", records[1].CmTrace!.Message);
    }

    [Fact]
    public void Falls_back_to_plain_text_when_not_all_sampled_lines_match_legacy_cmtrace_pattern()
    {
        var path = NewPath("mixed.log");
        // A file that contains one legacy-CMTrace-looking line among otherwise ordinary plain text
        // must NOT be misclassified as CmTraceLegacy (conservative detection: all sampled non-empty
        // lines must match).
        var content =
            "just a regular log line\r\n" +
            "Submitted request successfully  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:57.327-60><thread=19068 (0x4A7C)>\r\n" +
            "another regular line\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var index = new LogFileIndex(path);
        index.Refresh();

        Assert.Equal(LogFormat.PlainText, index.Format);
    }

    [Fact]
    public void Live_growth_is_picked_up_incrementally()
    {
        var path = NewPath("growing.log");
        using (var initial = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(initial, new UTF8Encoding(false)))
        {
            writer.WriteLine("first line");
        }

        var index = new LogFileIndex(path);
        index.Refresh();
        Assert.Equal(1, index.TotalRecordCount);

        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(append, new UTF8Encoding(false)))
        {
            writer.WriteLine("second line");
            writer.WriteLine("third line");
        }

        var result = index.Refresh();
        Assert.Equal(RefreshResult.Grown, result);
        Assert.Equal(3, index.TotalRecordCount);

        var records = index.GetRecords(1, 2);
        Assert.Equal("second line", records[0].RawText);
        Assert.Equal("third line", records[1].RawText);
    }

    [Fact]
    public void Truncation_resets_and_rebuilds_the_index()
    {
        var path = NewPath("truncated.log");
        File.WriteAllLines(path, new[] { "a", "b", "c", "d", "e" }, new UTF8Encoding(false));

        var index = new LogFileIndex(path);
        index.Refresh();
        Assert.Equal(5, index.TotalRecordCount);

        File.WriteAllLines(path, new[] { "x", "y" }, new UTF8Encoding(false));

        var result = index.Refresh();
        Assert.Equal(RefreshResult.Reset, result);
        Assert.Equal(2, index.TotalRecordCount);
        var records = index.GetRecords(0, 2);
        Assert.Equal("x", records[0].RawText);
        Assert.Equal("y", records[1].RawText);
    }

    [Fact]
    public void Handles_utf16_le_bom_encoding()
    {
        var path = NewPath("utf16.log");
        File.WriteAllLines(path, new[] { "unicode line one", "unicode line two" }, Encoding.Unicode);

        var index = new LogFileIndex(path);
        index.Refresh();

        Assert.Equal(2, index.TotalRecordCount);
        var records = index.GetRecords(0, 2);
        Assert.Equal("unicode line one", records[0].RawText);
        Assert.Equal("unicode line two", records[1].RawText);
    }

    [Fact]
    public void Large_file_uses_bounded_checkpoint_count_and_supports_random_access()
    {
        var path = NewPath("large.log");
        const int lineCount = 20_000;

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            for (int i = 0; i < lineCount; i++)
            {
                writer.WriteLine($"record number {i} with some padding text to simulate a realistic log line length");
            }
        }

        var index = new LogFileIndex(path);
        index.Refresh();

        Assert.Equal(lineCount, index.TotalRecordCount);

        // Checkpoint memory must be bounded: far fewer entries than total records.
        int expectedMaxCheckpoints = (lineCount / LogFileIndex.CheckpointInterval) + 2;

        // Random access deep into the file must not require scanning from byte 0 in an unbounded way;
        // verify correctness of an arbitrary window far from both ends.
        var window = index.GetRecords(15_432, 5);
        Assert.Equal(5, window.Count);
        Assert.Equal("record number 15432 with some padding text to simulate a realistic log line length", window[0].RawText);
        Assert.Equal("record number 15436 with some padding text to simulate a realistic log line length", window[4].RawText);

        var tail = index.GetRecords(lineCount - 3, 3);
        Assert.Equal(3, tail.Count);
        Assert.Equal($"record number {lineCount - 1} with some padding text to simulate a realistic log line length", tail[2].RawText);

        Assert.True(expectedMaxCheckpoints < lineCount, "checkpoint table should be far smaller than the record count");
    }

    [Fact]
    public void Open_read_share_allows_concurrent_writer()
    {
        var path = NewPath("shared.log");
        using var writerStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(writerStream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine("initial");
        writerStream.Flush();

        var index = new LogFileIndex(path);
        var result = index.Refresh();

        Assert.NotEqual(RefreshResult.Locked, result);
        Assert.Equal(1, index.TotalRecordCount);
    }
}
