using System.Text;
using LogViewer.Core.Encodings;
using LogViewer.Core.Parsing;
using LogViewer.Core.Records;

namespace LogViewer.Core.IO;

/// <summary>
/// Bounded-memory, incrementally-built index over a (possibly multi-gigabyte, possibly growing)
/// log file. Only a sparse "checkpoint" table of (record index -> byte offset) is kept in memory;
/// actual record text is always re-read from disk on demand for the requested window. This lets the
/// UI virtualize scrolling over files far larger than available RAM, and lets the tail keep growing
/// live without re-scanning or re-loading everything already seen.
/// </summary>
public sealed class LogFileIndex
{
    /// <summary>Every Nth record's byte offset is retained, bounding checkpoint memory to O(TotalRecordCount / CheckpointInterval).</summary>
    public const int CheckpointInterval = 2048;

    private const int ReadBufferSize = 64 * 1024;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<(long RecordIndex, long ByteOffset)> _checkpoints = new();

    private System.Text.Encoding _encoding = new UTF8Encoding(false);
    private int _preambleLength;
    private byte[] _newLineBytes = { 0x0A };
    private int _unitSize = 1;
    private byte[] _recordStartPrefixBytes = Array.Empty<byte>();

    private long _nextReadOffset;
    private long _currentRecordStartOffset;
    private bool _currentRecordIsMultilineCapable;
    private bool _hasOpenRecord;
    private long _finalizedRecordCount;
    private DateTime _observedCreationTimeUtc;
    private bool _initialized;

    public LogFileIndex(string path)
    {
        _path = path;
    }

    public LogFormat Format { get; private set; } = LogFormat.PlainText;

    public System.Text.Encoding Encoding => _encoding;

    /// <summary>Number of records observed so far, including a still-growing final record.</summary>
    public long TotalRecordCount => _finalizedRecordCount + (_hasOpenRecord ? 1 : 0);

    /// <summary>Byte length of the file as of the last successful refresh.</summary>
    public long IndexedByteLength { get; private set; }

    /// <summary>Re-scans any bytes appended since the previous call, or resets and rebuilds if the file shrank or was replaced.</summary>
    public RefreshResult Refresh()
    {
        lock (_lock)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(_path);
                if (!info.Exists)
                {
                    return RefreshResult.Missing;
                }
            }
            catch (IOException)
            {
                return RefreshResult.Locked;
            }

            FileStream stream;
            try
            {
                stream = LogFileReader.Open(_path);
            }
            catch (LogFileLockedException)
            {
                return RefreshResult.Locked;
            }

            using (stream)
            {
                var length = stream.Length;
                var creationTimeUtc = info.CreationTimeUtc;

                bool needsReset = !_initialized
                    || length < _nextReadOffset
                    || (_observedCreationTimeUtc != default && creationTimeUtc != _observedCreationTimeUtc);

                if (needsReset)
                {
                    ResetState();
                    _observedCreationTimeUtc = creationTimeUtc;
                    DetectEncodingAndFormat(stream);
                    _initialized = true;
                }

                if (length == _nextReadOffset)
                {
                    IndexedByteLength = length;
                    return needsReset ? RefreshResult.Reset : RefreshResult.NoChange;
                }

                ScanAppendedData(stream, length);
                IndexedByteLength = length;
                return needsReset ? RefreshResult.Reset : RefreshResult.Grown;
            }
        }
    }

    private void ResetState()
    {
        _checkpoints.Clear();
        _nextReadOffset = 0;
        _currentRecordStartOffset = 0;
        _currentRecordIsMultilineCapable = false;
        _hasOpenRecord = false;
        _finalizedRecordCount = 0;
        IndexedByteLength = 0;
    }

    private void DetectEncodingAndFormat(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[Math.Min(4096, stream.Length)];
        int read = stream.Read(head, 0, head.Length);

        var detected = LogEncodingDetector.Detect(head, read);
        _encoding = detected.Encoding;
        _preambleLength = detected.PreambleLength;
        _newLineBytes = LogEncodingDetector.NewLineBytes(_encoding);
        _unitSize = LogEncodingDetector.UnitSize(_encoding);
        _recordStartPrefixBytes = BuildPrefixBytes("<![LOG[", _encoding);

        _nextReadOffset = _preambleLength;
        _currentRecordStartOffset = _preambleLength;
        _checkpoints.Add((0, _preambleLength));

        // Sample the first handful of lines with the string-based detector (cheap, bounded cost) to
        // decide which CMTrace variant(s), if any, this file predominantly uses. This label only
        // drives the summary shown to the user (e.g. status bar text) - actual per-record parsing
        // below (see EmitRecord) always tries both variants independently for every record, so a file
        // that mixes both variants (or is mislabeled here) still gets each record parsed correctly.
        stream.Seek(_preambleLength, SeekOrigin.Begin);
        var sampleBuffer = new byte[Math.Min(ReadBufferSize, Math.Max(0, stream.Length - _preambleLength))];
        int sampleRead = sampleBuffer.Length > 0 ? stream.Read(sampleBuffer, 0, sampleBuffer.Length) : 0;
        var lines = LineSplitter.SplitComplete(sampleBuffer.AsSpan(0, sampleRead), _preambleLength, _newLineBytes, _unitSize, out _);

        int checkedLines = 0;
        int xmlMatchLines = 0;
        int legacyCandidateLines = 0;
        int legacyMatchLines = 0;
        foreach (var line in lines)
        {
            if (checkedLines++ >= 20)
            {
                break;
            }

            var text = DecodeLine(sampleBuffer, (int)(line.AbsoluteOffset - _preambleLength), line.LengthExcludingTerminator);
            if (text.Length == 0)
            {
                continue;
            }

            if (CmTraceParser.IsRecordStart(text))
            {
                xmlMatchLines++;
                continue;
            }

            legacyCandidateLines++;
            if (CmTraceParser.IsLegacyRecordLine(text))
            {
                legacyMatchLines++;
            }
        }

        // The XML variant has a distinctive line-start marker, so a single matching line is enough to
        // conclude the file uses it (a continuation line of the same record just won't have the
        // marker, which is fine - it doesn't need to "vote" for the format). The trailing-metadata
        // variant has no distinctive marker, so - purely for this summary label - we require every
        // non-XML sampled line to match it, to avoid mislabeling plain text that merely happens to
        // contain something resembling "$$<...>" once as CmTraceLegacy.
        bool looksLikeXml = xmlMatchLines > 0;
        bool looksLikeLegacy = legacyCandidateLines > 0 && legacyMatchLines == legacyCandidateLines;

        Format = (looksLikeXml, looksLikeLegacy) switch
        {
            (true, true) => LogFormat.CmTraceMixed,
            (true, false) => LogFormat.CmTrace,
            (false, true) => LogFormat.CmTraceLegacy,
            _ => LogFormat.PlainText,
        };
    }

    private static byte[] BuildPrefixBytes(string ascii, System.Text.Encoding encoding)
    {
        return encoding.GetBytes(ascii);
    }

    private string DecodeLine(byte[] buffer, int offset, int length) => _encoding.GetString(buffer, offset, length);

    private void ScanAppendedData(FileStream stream, long targetLength)
    {
        stream.Seek(_nextReadOffset, SeekOrigin.Begin);
        var buffer = new byte[ReadBufferSize];

        while (_nextReadOffset < targetLength)
        {
            int toRead = (int)Math.Min(buffer.Length, targetLength - _nextReadOffset);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            long readStartOffset = _nextReadOffset;
            var lines = LineSplitter.SplitComplete(buffer.AsSpan(0, read), readStartOffset, _newLineBytes, _unitSize, out int incompleteTail);

            foreach (var line in lines)
            {
                // Boundary detection is per-record, not driven by the file-wide Format label: a line
                // starting with the XML CMTrace marker always starts a new (possibly multi-line)
                // record. Otherwise, a new record starts unless we're in the middle of a still-open
                // multiline-capable (XML-style) record AND this line doesn't, on its own, fully match
                // the single-line trailing-metadata pattern - a full match there means the file has
                // switched (or interleaves) to the other CMTrace variant, so this line must be treated
                // as a fresh record boundary rather than a continuation of the open XML message.
                int localOffset = (int)(line.AbsoluteOffset - readStartOffset);
                bool hasXmlPrefix = HasPrefix(buffer, localOffset, line.LengthExcludingTerminator);
                bool isStart = hasXmlPrefix
                    || !_hasOpenRecord
                    || !_currentRecordIsMultilineCapable
                    || CmTraceParser.IsLegacyRecordLine(_encoding.GetString(buffer, localOffset, line.LengthExcludingTerminator));

                if (isStart)
                {
                    if (_hasOpenRecord)
                    {
                        FinalizeOpenRecord(line.AbsoluteOffset);
                    }

                    _currentRecordStartOffset = line.AbsoluteOffset;
                    _currentRecordIsMultilineCapable = hasXmlPrefix;
                    _hasOpenRecord = true;
                }
            }

            _nextReadOffset += read - incompleteTail;

            bool reachedCurrentEnd = readStartOffset + read >= targetLength;
            if (incompleteTail > 0)
            {
                // Always resync the stream position to the confirmed line boundary before the next
                // iteration (or the next Refresh call), whether or not we stop now.
                stream.Seek(_nextReadOffset, SeekOrigin.Begin);
                if (reachedCurrentEnd)
                {
                    // No more bytes available right now; the trailing partial line will be
                    // completed on a later Refresh once the writer appends more data.
                    break;
                }
            }
        }
    }

    private bool HasPrefix(byte[] buffer, int offset, int length)
    {
        if (_recordStartPrefixBytes.Length == 0 || length < _recordStartPrefixBytes.Length)
        {
            return false;
        }

        for (int i = 0; i < _recordStartPrefixBytes.Length; i++)
        {
            if (buffer[offset + i] != _recordStartPrefixBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    private void FinalizeOpenRecord(long endOffset)
    {
        if (_finalizedRecordCount % CheckpointInterval == 0)
        {
            // Checkpoint already exists for record 0 from initialization; avoid a duplicate.
            if (_finalizedRecordCount != 0 || _checkpoints.Count == 0)
            {
                _checkpoints.Add((_finalizedRecordCount, _currentRecordStartOffset));
            }
        }

        _finalizedRecordCount++;
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> records starting at logical index <paramref name="startIndex"/>.
    /// Always re-scans from disk starting at the nearest known checkpoint, so memory use is bounded by
    /// the checkpoint table plus the requested window, never by total file size.
    /// </summary>
    public IReadOnlyList<LogRecord> GetRecords(long startIndex, int count, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return GetRecordsCore(startIndex, count, cancellationToken);
        }
    }

    private IReadOnlyList<LogRecord> GetRecordsCore(long startIndex, int count, CancellationToken cancellationToken)
    {
        if (!_initialized || startIndex < 0 || count <= 0)
        {
            return Array.Empty<LogRecord>();
        }

        var checkpoint = FindCheckpointAtOrBefore(startIndex);

        using var stream = LogFileReader.Open(_path);
        stream.Seek(checkpoint.ByteOffset, SeekOrigin.Begin);

        var results = new List<LogRecord>(count);
        var buffer = new byte[ReadBufferSize];

        long recordIndex = checkpoint.RecordIndex;
        long recordStartOffset = checkpoint.ByteOffset;
        bool haveOpenRecord = false;
        bool currentRecordIsMultilineCapable = false;
        List<byte>? accumulator = null;

        long readCursor = checkpoint.ByteOffset;
        long targetLength = IndexedByteLength;

        void EmitRecord(long endOffset)
        {
            if (recordIndex >= startIndex)
            {
                var bytes = accumulator?.ToArray() ?? Array.Empty<byte>();
                var rawText = _encoding.GetString(bytes);
                // Per-record format dispatch (not driven by the file-wide Format label): try the XML
                // CMTrace variant first when the record actually carries its marker, otherwise try the
                // trailing-metadata variant. This means a file that mixes both CMTrace variants (or a
                // file whose whole-file summary label under-detected one variant) still gets every
                // individual record parsed correctly according to what it actually is.
                CmTraceFields? cmTrace = rawText.StartsWith("<![LOG[", StringComparison.Ordinal)
                    ? CmTraceParser.TryParse(rawText)
                    : CmTraceParser.TryParseLegacy(rawText);
                results.Add(new LogRecord
                {
                    Index = recordIndex,
                    ByteOffset = recordStartOffset,
                    ByteLength = (int)(endOffset - recordStartOffset),
                    RawText = rawText,
                    CmTrace = cmTrace,
                });
            }

            recordIndex++;
        }

        while (readCursor < targetLength && results.Count < count && !cancellationToken.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buffer.Length, targetLength - readCursor);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            var lines = LineSplitter.SplitComplete(buffer.AsSpan(0, read), readCursor, _newLineBytes, _unitSize, out int incompleteTail);

            foreach (var line in lines)
            {
                int localOffset = (int)(line.AbsoluteOffset - readCursor);
                bool hasXmlPrefix = HasPrefix(buffer, localOffset, line.LengthExcludingTerminator);
                bool isStart = hasXmlPrefix
                    || !haveOpenRecord
                    || !currentRecordIsMultilineCapable
                    || CmTraceParser.IsLegacyRecordLine(_encoding.GetString(buffer, localOffset, line.LengthExcludingTerminator));

                if (isStart)
                {
                    if (haveOpenRecord)
                    {
                        EmitRecord(line.AbsoluteOffset);
                        if (results.Count >= count)
                        {
                            return results;
                        }
                    }

                    recordStartOffset = line.AbsoluteOffset;
                    haveOpenRecord = true;
                    currentRecordIsMultilineCapable = hasXmlPrefix;
                    accumulator = recordIndex >= startIndex ? new List<byte>(line.LengthExcludingTerminator + 16) : null;
                }
                else if (recordIndex >= startIndex && accumulator != null)
                {
                    // Join continuation lines of the same (e.g. CMTrace) record with a single '\n',
                    // never emitting the physical line's own trailing terminator bytes.
                    accumulator.AddRange(_newLineBytes);
                }

                if (recordIndex >= startIndex)
                {
                    accumulator ??= new List<byte>();
                    for (int i = 0; i < line.LengthExcludingTerminator; i++)
                    {
                        accumulator.Add(buffer[localOffset + i]);
                    }
                }
            }

            readCursor += read;
            bool reachedCurrentEnd = readCursor >= targetLength;
            if (incompleteTail > 0)
            {
                readCursor -= incompleteTail;
                stream.Seek(readCursor, SeekOrigin.Begin);
                if (reachedCurrentEnd)
                {
                    break;
                }
            }
        }

        // Emit the trailing open record (file's current last record, possibly still growing).
        if (haveOpenRecord && results.Count < count)
        {
            EmitRecord(targetLength);
        }

        return results;
    }

    private (long RecordIndex, long ByteOffset) FindCheckpointAtOrBefore(long recordIndex)
    {
        var result = _checkpoints[0];
        foreach (var cp in _checkpoints)
        {
            if (cp.RecordIndex > recordIndex)
            {
                break;
            }

            result = cp;
        }

        return result;
    }
}
