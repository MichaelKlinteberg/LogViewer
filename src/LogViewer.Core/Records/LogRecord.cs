namespace LogViewer.Core.Records;

/// <summary>Severity as understood by the CMTrace format (1=Info,2=Warning,3=Error). Plain text logs are always Info.</summary>
public enum LogSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// A single logical log record. For plain text logs this is exactly one physical line.
/// For CMTrace logs this may span multiple physical lines (e.g. embedded stack traces),
/// in which case <see cref="RawText"/> contains all of them joined by '\n'.
/// </summary>
public sealed class LogRecord
{
    /// <summary>0-based logical record index within the file.</summary>
    public required long Index { get; init; }

    /// <summary>Byte offset of the first byte of this record in the underlying file.</summary>
    public required long ByteOffset { get; init; }

    /// <summary>Total length in bytes of this record, including any embedded continuation lines and line terminators.</summary>
    public required int ByteLength { get; init; }

    /// <summary>The raw, undecoded-for-CMTrace text exactly as it appears in the file (decoded from bytes to string only).</summary>
    public required string RawText { get; init; }

    /// <summary>Parsed CMTrace fields, or null if the record is plain text (not a recognized CMTrace line) or the file is treated as plain text.</summary>
    public CmTraceFields? CmTrace { get; init; }
}

/// <summary>Fields extracted from a CMTrace-formatted log line.</summary>
public sealed record CmTraceFields(
    string Message,
    string Component,
    LogSeverity Severity,
    string Context,
    string Type,
    string Thread,
    string File,
    DateTimeOffset? Timestamp,
    string RawTimeText,
    string RawDateText);
