namespace LogViewer.Core.IO;

/// <summary>Detected logical layout of a log file.</summary>
public enum LogFormat
{
    PlainText,
    CmTrace,
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
