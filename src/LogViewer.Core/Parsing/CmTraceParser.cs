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

    /// <summary>True if the given (first) physical line starts a new CMTrace record.</summary>
    public static bool IsRecordStart(ReadOnlySpan<char> firstLine)
    {
        return firstLine.StartsWith("<![LOG[", StringComparison.Ordinal);
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
