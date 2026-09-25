namespace LogViewer.Core.IO;

/// <summary>Thrown when a log file cannot be opened because another process holds an incompatible lock on it.</summary>
public sealed class LogFileLockedException : IOException
{
    public LogFileLockedException(string path, Exception inner)
        : base($"The file \"{path}\" is currently locked by another process and cannot be opened for reading.", inner)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Opens log files read-only, sharing full read/write/delete access with any writer, and never
/// mutates the file. Wraps sharing-violation failures into <see cref="LogFileLockedException"/> so
/// callers can present a friendly retry prompt instead of crashing.
/// </summary>
public static class LogFileReader
{
    public static FileStream Open(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new LogFileLockedException(path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LogFileLockedException(path, ex);
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33
        int hr = ex.HResult & 0xFFFF;
        return hr is 32 or 33;
    }
}
