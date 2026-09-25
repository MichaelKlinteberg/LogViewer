namespace LogViewer.Core.IO;

/// <summary>Detected logical layout of a log file.</summary>
public enum LogFormat
{
    PlainText,

    /// <summary>The XML-attribute CMTrace format: <c>&lt;![LOG[message]LOG]!&gt;&lt;time="..." date="..." ...&gt;</c>.</summary>
    CmTrace,

    /// <summary>
    /// The trailing-metadata CMTrace format still actively emitted by many current SCCM/ConfigMgr
    /// client components (e.g. ccmexec.log), where each line ends with metadata instead of starting
    /// with an XML-like tag:
    /// <c>message~  $$&lt;Component&gt;&lt;MM-dd-yyyy HH:mm:ss.fff+/-offsetMinutes&gt;&lt;thread=1234 (0x4D2)&gt;</c>.
    /// This is a first-class, actively-used CMTrace variant, not a deprecated fallback - it is
    /// detected and parsed with the same priority as <see cref="CmTrace"/>. Unlike <see cref="CmTrace"/>,
    /// records in this variant are always exactly one physical line (no embedded multi-line messages).
    /// </summary>
    CmTraceLegacy,

    /// <summary>
    /// The sampled lines contain a mix of both CMTrace variants (<see cref="CmTrace"/> and
    /// <see cref="CmTraceLegacy"/>). Each record is still detected and parsed individually according to
    /// whichever variant it actually matches - this label only affects the summary text shown to the
    /// user (e.g. in the status bar); it does not change per-record parsing behavior.
    /// </summary>
    CmTraceMixed,
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
