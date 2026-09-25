using LogViewer.Core.Parsing;
using LogViewer.Core.Records;
using Xunit;

namespace LogViewer.Core.Tests;

public class CmTraceParserTests
{
    private const string SampleLine =
        "<![LOG[Download started for content ABC123]LOG]!><time=\"10:23:45.678+120\" date=\"09-25-2026\" component=\"ContentTransferManager\" context=\"\" type=\"1\" thread=\"1234\" file=\"ctm.cpp\">";

    [Fact]
    public void IsRecordStart_recognizes_cmtrace_prefix()
    {
        Assert.True(CmTraceParser.IsRecordStart(SampleLine.AsSpan()));
        Assert.False(CmTraceParser.IsRecordStart("plain text line".AsSpan()));
    }

    [Fact]
    public void TryParse_extracts_all_fields()
    {
        var fields = CmTraceParser.TryParse(SampleLine);

        Assert.NotNull(fields);
        Assert.Equal("Download started for content ABC123", fields!.Message);
        Assert.Equal("ContentTransferManager", fields.Component);
        Assert.Equal(LogSeverity.Info, fields.Severity);
        Assert.Equal("1234", fields.Thread);
        Assert.Equal("ctm.cpp", fields.File);
        Assert.NotNull(fields.Timestamp);
        Assert.Equal(2026, fields.Timestamp!.Value.Year);
        Assert.Equal(9, fields.Timestamp.Value.Month);
        Assert.Equal(25, fields.Timestamp.Value.Day);
        Assert.Equal(10, fields.Timestamp.Value.Hour);
        Assert.Equal(TimeSpan.FromMinutes(120), fields.Timestamp.Value.Offset);
    }

    [Theory]
    [InlineData("2", LogSeverity.Warning)]
    [InlineData("3", LogSeverity.Error)]
    [InlineData("1", LogSeverity.Info)]
    [InlineData("bogus", LogSeverity.Info)]
    public void TryParse_maps_severity(string typeValue, LogSeverity expected)
    {
        var line = $"<![LOG[msg]LOG]!><time=\"10:00:00.000+0\" date=\"01-01-2026\" component=\"c\" context=\"\" type=\"{typeValue}\" thread=\"1\" file=\"f\">";
        var fields = CmTraceParser.TryParse(line);

        Assert.NotNull(fields);
        Assert.Equal(expected, fields!.Severity);
    }

    [Fact]
    public void TryParse_returns_null_for_non_cmtrace_text()
    {
        Assert.Null(CmTraceParser.TryParse("just a regular log line, nothing special"));
    }

    [Fact]
    public void TryParse_handles_embedded_newlines_in_message()
    {
        var line = "<![LOG[line one\r\nline two continuation]LOG]!><time=\"01:02:03.000+0\" date=\"01-01-2026\" component=\"c\" context=\"\" type=\"1\" thread=\"1\" file=\"f\">";
        var fields = CmTraceParser.TryParse(line);

        Assert.NotNull(fields);
        Assert.Contains("line two continuation", fields!.Message);
    }
}
