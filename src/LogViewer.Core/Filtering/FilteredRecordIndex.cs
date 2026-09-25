using LogViewer.Core.IO;
using LogViewer.Core.Records;

namespace LogViewer.Core.Filtering;

/// <summary>
/// Wraps a <see cref="LogFileIndex"/> with an active <see cref="FilterState"/> and exposes a
/// "visible index" space: when Filter/Hide are both empty this is a zero-cost passthrough over the
/// full record range (so the common, filter-less case scales to multi-gigabyte files with no extra
/// memory at all). When a Filter or Hide is active, a compact list of matching underlying record
/// indices (8 bytes each) is built incrementally in bounded batches via <see cref="AdvanceMatchBuilding"/>,
/// so callers (e.g. a background loop) can keep scanning the whole file - including parts far behind
/// the current view - without blocking the UI thread and without ever materializing full record text
/// for non-matching rows. Memory therefore scales with the number of matches, not with file size.
/// </summary>
public sealed class FilteredRecordIndex
{
    private readonly LogFileIndex _index;
    private readonly object _lock = new();
    private readonly List<long> _matches = new();
    private FilterState _state = new();

    public FilteredRecordIndex(LogFileIndex index)
    {
        _index = index;
    }

    public FilterState State
    {
        get { lock (_lock) { return _state; } }
        set
        {
            lock (_lock)
            {
                _state = value;
                _matches.Clear();
                ScannedRecordCount = 0;
            }
        }
    }

    public bool IsFiltering { get { lock (_lock) { return _state.HasFilter || _state.HasHide; } } }

    /// <summary>Number of underlying records already examined by the match builder.</summary>
    public long ScannedRecordCount { get; private set; }

    /// <summary>True once the match builder has caught up with everything the underlying index currently knows about.</summary>
    public bool IsFullyScanned { get { lock (_lock) { return !IsFiltering || ScannedRecordCount >= _index.TotalRecordCount; } } }

    /// <summary>Number of matches found so far (only meaningful while <see cref="IsFiltering"/>).</summary>
    public long MatchCount { get { lock (_lock) { return _matches.Count; } } }

    /// <summary>Rows visible to the UI: either all records (no Filter/Hide active) or the matches found so far.</summary>
    public long VisibleCount { get { lock (_lock) { return IsFiltering ? _matches.Count : _index.TotalRecordCount; } } }

    /// <summary>
    /// Scans forward from where match building last left off, reading at most one batch of
    /// <paramref name="batchSize"/> records. Call this repeatedly (e.g. from a background loop,
    /// checking <see cref="IsFullyScanned"/> and <paramref name="cancellationToken"/> between calls)
    /// to build up the full match list without blocking for longer than one batch at a time.
    /// </summary>
    public void AdvanceMatchBuilding(int batchSize, CancellationToken cancellationToken = default)
    {
        FilterState stateSnapshot;
        long scanned;
        lock (_lock)
        {
            if (!IsFiltering)
            {
                ScannedRecordCount = _index.TotalRecordCount;
                return;
            }

            if (ScannedRecordCount >= _index.TotalRecordCount || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            stateSnapshot = _state;
            scanned = ScannedRecordCount;
        }

        long total = _index.TotalRecordCount;
        int take = (int)Math.Min(batchSize, total - scanned);
        // GetRecords performs the (potentially slow) disk I/O outside the lock so concurrent readers
        // (e.g. the UI thread loading a display window) are not blocked while a batch is scanned.
        var records = _index.GetRecords(scanned, take, cancellationToken);
        if (records.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var record in records)
            {
                if (FilterEngine.Evaluate(record, stateSnapshot).Visible)
                {
                    _matches.Add(record.Index);
                }
            }

            ScannedRecordCount += records.Count;
        }
    }

    /// <summary>Notifies the builder that the underlying index grew, so a subsequent AdvanceMatchBuilding call resumes scanning the new tail.</summary>
    public void OnUnderlyingIndexReset()
    {
        lock (_lock)
        {
            _matches.Clear();
            ScannedRecordCount = 0;
        }
    }

    /// <summary>Retrieves the record at the given visible-row position, re-reading it from disk on demand.</summary>
    public LogRecord? GetVisible(long visibleIndex)
    {
        if (visibleIndex < 0)
        {
            return null;
        }

        bool filtering;
        long underlyingIndex;
        lock (_lock)
        {
            filtering = IsFiltering;
            if (!filtering)
            {
                underlyingIndex = visibleIndex;
            }
            else
            {
                if (visibleIndex >= _matches.Count)
                {
                    return null;
                }

                underlyingIndex = _matches[(int)visibleIndex];
            }
        }

        if (!filtering && underlyingIndex >= _index.TotalRecordCount)
        {
            return null;
        }

        var records = _index.GetRecords(underlyingIndex, 1);
        return records.Count > 0 ? records[0] : null;
    }

    /// <summary>
    /// Retrieves a contiguous window of visible rows. When no Filter/Hide is active this is a single
    /// sequential disk scan (fast, bounded by the checkpoint interval); while filtering it re-reads
    /// each matched underlying record individually since matches are not necessarily contiguous.
    /// </summary>
    public IReadOnlyList<LogRecord> GetVisibleRange(long startVisibleIndex, int count)
    {
        if (startVisibleIndex < 0 || count <= 0)
        {
            return Array.Empty<LogRecord>();
        }

        long[] underlyingIndices;
        lock (_lock)
        {
            if (!IsFiltering)
            {
                underlyingIndices = Array.Empty<long>(); // sentinel: use direct sequential read below
            }
            else
            {
                long end = Math.Min(startVisibleIndex + count, _matches.Count);
                if (end <= startVisibleIndex)
                {
                    return Array.Empty<LogRecord>();
                }

                underlyingIndices = new long[end - startVisibleIndex];
                for (long i = startVisibleIndex; i < end; i++)
                {
                    underlyingIndices[i - startVisibleIndex] = _matches[(int)i];
                }
            }
        }

        if (underlyingIndices.Length == 0 && !IsFiltering)
        {
            return _index.GetRecords(startVisibleIndex, count);
        }

        var results = new List<LogRecord>(underlyingIndices.Length);
        foreach (var underlyingIndex in underlyingIndices)
        {
            var records = _index.GetRecords(underlyingIndex, 1);
            if (records.Count > 0)
            {
                results.Add(records[0]);
            }
        }

        return results;
    }
}
