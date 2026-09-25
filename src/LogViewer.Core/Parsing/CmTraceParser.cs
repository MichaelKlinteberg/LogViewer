using System.Globalization;
using System.Text.RegularExpressions;
using LogViewer.Core.Records;

namespace LogViewer.Core.Parsing;

/// <summary>
/// Parses the CMTrace log line format used by Microsoft Endpoint Configuration Manager
/// (and many other Microsoft deployment tools):
/// <c>&lt;![LOG[message]LOG]!&gt;&lt;time="HH:mm:ss.fff+UUU" date="MM-dd-yyyy" component="..." context="" type="1" thread="1234" file="source.cpp"&gt;</c>
/// </summary>
public static partial class CmTraceParser
{
    // Compiled once; Singleline lets '.' match embedded newlines in the message body.
    private static readonly Regex LinePattern = new(
        @"^<!\[LOG\[(?<msg>.*?)\]LOG\]!><time=""(?<time>[^""]*)""\s*date=""(?<date>[^""]*)""\s*component=""(?<component>[^""]*)""\s*context=""(?<context>[^""]*)""\s*type=""(?<type>[^""]*)""\s*thread=""(?<thread>[^""]*)""\s*file=""(?<file>[^""]*)""\s*>\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // The legacy/older CMTrace line format (still emitted by many SCCM/ccmexec client logs), e.g.:
    //   SspiExcludePackage succeeded for .\Administrator authentication!~  $$<SMS_CLIENT_CONFIG_MANAGER><09-25-2026 15:13:55.938-60><thread=18612 (0x48B4)>
    // The message is everything before the trailing "$$<Component><timestamp><thread=...>" suffix;
    // an optional literal '~' directly precedes the whitespace before "$$<" and is not part of the
    // message. The lazy "(?<msg>.*?)" finds the earliest split point where the rest of the pattern
    // matches all the way to the end of the line, which - because a valid trailing metadata block
    // must reach end-of-line - correctly lands on the real (last) "$$<...>" suffix even if the
    // message text itself happens to contain an unrelated "$$<" substring somewhere earlier.
    private static readonly Regex LegacyLinePattern = new(
        @"^(?<msg>.*?)~?\s*\$\$<(?<component>[^>]*)><(?<timestamp>[^>]*)><thread=(?<thread>\d+)\s*\([^)]*\)>\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>True if the given (first) physical line starts a new CMTrace record.</summary>
    public static bool IsRecordStart(ReadOnlySpan<char> firstLine)
    {
        return firstLine.StartsWith("<![LOG[", StringComparison.Ordinal);
    }

    /// <summary>True if the given complete physical line matches the legacy (trailing-metadata) CMTrace format.</summary>
    public static bool IsLegacyRecordLine(ReadOnlySpan<char> line)
    {
        return LegacyLinePattern.IsMatch(line);
    }

    /// <summary>Attempts to parse a complete (possibly multi-line) raw record as a CMTrace entry.</summary>
    public static CmTraceFields? TryParse(string rawText)
    {
        var match = LinePattern.Match(rawText);
        if (!match.Success)
        {
            return null;
        }

        var severity = match.Groups["type"].Value switch
        {
            "2" => LogSeverity.Warning,
            "3" => LogSeverity.Error,
            _ => LogSeverity.Info,
        };

        var timeText = match.Groups["time"].Value;
        var dateText = match.Groups["date"].Value;
        DateTimeOffset? timestamp = TryParseTimestamp(dateText, timeText);

        return new CmTraceFields(
            Message: match.Groups["msg"].Value,
            Component: match.Groups["component"].Value,
            Severity: severity,
            Context: match.Groups["context"].Value,
            Type: match.Groups["type"].Value,
            Thread: match.Groups["thread"].Value,
            File: match.Groups["file"].Value,
            Timestamp: timestamp,
            RawTimeText: timeText,
            RawDateText: dateText);
    }

    /// <summary>Attempts to parse a single physical line as a legacy-format CMTrace entry (see <see cref="IsLegacyRecordLine"/>).</summary>
    public static CmTraceFields? TryParseLegacy(string rawText)
    {
        var match = LegacyLinePattern.Match(rawText);
        if (!match.Success)
        {
            return null;
        }

        // Combined field is "MM-dd-yyyy HH:mm:ss.fff+/-offsetMinutes"; split into date/time (with
        // offset still attached to the time part) so we can reuse the same offset-in-minutes parsing
        // logic as the modern CMTrace format's time="...+/-NNN" attribute.
        var combined = match.Groups["timestamp"].Value;
        var spaceIndex = combined.IndexOf(' ');
        var dateText = spaceIndex >= 0 ? combined[..spaceIndex] : combined;
        var timeText = spaceIndex >= 0 ? combined[(spaceIndex + 1)..] : string.Empty;

        DateTimeOffset? timestamp = TryParseTimestamp(dateText, timeText);

        return new CmTraceFields(
            Message: match.Groups["msg"].Value,
            Component: match.Groups["component"].Value,
            Severity: LogSeverity.Info, // The legacy format has no severity/type field.
            Context: string.Empty,
            Type: string.Empty,
            Thread: match.Groups["thread"].Value,
            File: string.Empty,
            Timestamp: timestamp,
            RawTimeText: timeText,
            RawDateText: dateText);
    }

    private static DateTimeOffset? TryParseTimestamp(string dateText, string timeText)
    {
        // time is like "10:23:45.678+120" or "10:23:45.678-60" where the trailing signed number is
        // the UTC offset in minutes.
        var offsetMinutes = 0;
        var timeCore = timeText;
        var signIndex = timeText.LastIndexOfAny(['+', '-']);
        if (signIndex > 0)
        {
            timeCore = timeText[..signIndex];
            if (int.TryParse(timeText[signIndex..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedOffset))
            {
                offsetMinutes = parsedOffset;
            }
        }

        if (!DateTime.TryParseExact(dateText, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var datePart))
        {
            return null;
        }

        if (!TimeSpan.TryParseExact(timeCore, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var timePart))
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(datePart + timePart, TimeSpan.FromMinutes(offsetMinutes));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
