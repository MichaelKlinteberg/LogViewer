namespace LogViewer.Core.IO;

/// <summary>Detected logical layout of a log file.</summary>
public enum LogFormat
{
    PlainText,

    /// <summary>The modern CMTrace format: <c>&lt;![LOG[message]LOG]!&gt;&lt;time="..." date="..." ...&gt;</c>.</summary>
    CmTrace,

    /// <summary>
    /// The older/legacy CMTrace format used by many SCCM/ccmexec client logs, where each line ends
    /// with trailing metadata instead of starting with an XML-like tag:
    /// <c>message~  $$&lt;Component&gt;&lt;MM-dd-yyyy HH:mm:ss.fff+/-offsetMinutes&gt;&lt;thread=1234 (0x4D2)&gt;</c>.
    /// Unlike <see cref="CmTrace"/>, records in this format are always exactly one physical line.
    /// </summary>
    CmTraceLegacy,
}

/// <summary>Outcome of a call to <see cref="LogFileIndex.Refresh"/>.</summary>
public enum RefreshResult
{
    /// <summary>File length unchanged since the last refresh.</summary>
    NoChange,

    /// <summary>New data was appended and indexed.</summary>
    Grown,

    /// <summary>
    /// The file was truncated or replaced (rotation). The index was reset and rebuilt from the
    /// start of the (new) file; any previously displayed content is stale.
    /// </summary>
    Reset,

    /// <summary>The file could not be opened (e.g. an exclusive writer lock). No change was made.</summary>
    Locked,

    /// <summary>The file no longer exists.</summary>
    Missing,
}
