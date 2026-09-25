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

    // The following lines are copied verbatim from a real ccmexec.log (SCCM client log) that uses
    // the older/legacy CMTrace format, where each line ends with trailing "$$<Component><timestamp>
    // <thread=...>" metadata instead of starting with a "<![LOG[" XML tag. Regression coverage for
    // https://github.com internal bug report: parsed columns (Time/Severity/Component/Thread) were
    // empty and the metadata leaked into the Message column for this variant.
    private const string LegacyLineWithTilde =
        "---> SspiExcludePackage succeeded for .\\Administrator authentication!~  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:55.938-60><thread=18612 (0x48B4)>";

    private const string LegacyLineWithoutTilde =
        "Submitted request successfully  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:57.327-60><thread=19068 (0x4A7C)>";

    private const string LegacyLineWithEmbeddedQuotesAndBackslashes =
        "---> Attempting to connect to administrative share '\\\\USCHQPR-VPR001.aveva.com\\admin$' using machine account.~  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:55.968-60><thread=18612 (0x48B4)>";

    [Fact]
    public void IsLegacyRecordLine_recognizes_trailing_metadata_format()
    {
        Assert.True(CmTraceParser.IsLegacyRecordLine(LegacyLineWithTilde.AsSpan()));
        Assert.True(CmTraceParser.IsLegacyRecordLine(LegacyLineWithoutTilde.AsSpan()));
        Assert.False(CmTraceParser.IsLegacyRecordLine("plain text line with no metadata".AsSpan()));
        Assert.False(CmTraceParser.IsLegacyRecordLine(SampleLine.AsSpan()));
    }

    [Fact]
    public void TryParseLegacy_extracts_fields_with_trailing_tilde()
    {
        var fields = CmTraceParser.TryParseLegacy(LegacyLineWithTilde);

        Assert.NotNull(fields);
        Assert.Equal("---> SspiExcludePackage succeeded for .\\Administrator authentication!", fields!.Message);
        Assert.Equal("SMS_CLIENT_CONFIG_MANAGER", fields.Component);
        Assert.Equal("18612", fields.Thread);
        Assert.Equal(LogSeverity.Info, fields.Severity);
        Assert.NotNull(fields.Timestamp);
        Assert.Equal(2026, fields.Timestamp!.Value.Year);
        Assert.Equal(9, fields.Timestamp.Value.Month);
        Assert.Equal(25, fields.Timestamp.Value.Day);
        Assert.Equal(15, fields.Timestamp.Value.Hour);
        Assert.Equal(13, fields.Timestamp.Value.Minute);
        Assert.Equal(55, fields.Timestamp.Value.Second);
        Assert.Equal(938, fields.Timestamp.Value.Millisecond);
        Assert.Equal(TimeSpan.FromMinutes(-60), fields.Timestamp.Value.Offset);
    }

    [Fact]
    public void TryParseLegacy_extracts_fields_without_trailing_tilde()
    {
        var fields = CmTraceParser.TryParseLegacy(LegacyLineWithoutTilde);

        Assert.NotNull(fields);
        Assert.Equal("Submitted request successfully", fields!.Message);
        Assert.Equal("SMS_CLIENT_CONFIG_MANAGER", fields.Component);
        Assert.Equal("19068", fields.Thread);
        Assert.NotNull(fields.Timestamp);
        Assert.Equal(57, fields.Timestamp!.Value.Second);
    }

    [Fact]
    public void TryParseLegacy_preserves_message_content_with_quotes_backslashes_and_arrows()
    {
        var fields = CmTraceParser.TryParseLegacy(LegacyLineWithEmbeddedQuotesAndBackslashes);

        Assert.NotNull(fields);
        Assert.Equal(
            "---> Attempting to connect to administrative share '\\\\USCHQPR-VPR001.aveva.com\\admin$' using machine account.",
            fields!.Message);
        Assert.Equal("SMS_CLIENT_CONFIG_MANAGER", fields.Component);
        Assert.Equal("18612", fields.Thread);
    }

    [Fact]
    public void TryParseLegacy_returns_null_for_non_matching_text()
    {
        Assert.Null(CmTraceParser.TryParseLegacy("just a regular log line, nothing special"));
        Assert.Null(CmTraceParser.TryParseLegacy(SampleLine));
    }
}
