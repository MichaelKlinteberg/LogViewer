using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using LogViewer.App.Commands;
using LogViewer.Core.Filtering;
using LogViewer.Core.IO;

namespace LogViewer.App.ViewModels;

/// <summary>
/// Per-tab state and background engine: owns the file's <see cref="LogFileIndex"/> and
/// <see cref="FilteredRecordIndex"/>, polls for growth/truncation/rotation on a background task, and
/// exposes everything the view needs (filter text boxes, raw/parsed + follow toggles, status, and
/// on-demand windowed row loading) without ever holding the whole file in memory.
/// </summary>
public sealed class LogTabViewModel : INotifyPropertyChanged, IDisposable
{
    private const int PollIntervalMs = 500;
    private const int DebounceMs = 250;
    private const int MatchBuildBatchSize = 4000;

    private readonly LogFileIndex _index;
    private readonly FilteredRecordIndex _filtered;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly DispatcherTimer _debounceTimer;
    private readonly Dispatcher _dispatcher;

    private string _highlightText = string.Empty;
    private string _hideText = string.Empty;
    private string _filterText = string.Empty;
    private bool _isRawView;
    private bool _isFollowing = true;
    private bool _isLocked;
    private string? _errorMessage;
    private string _statusText = string.Empty;
    private long _lastKnownVisibleCount;

    public LogTabViewModel(string filePath, string? initialHighlight = null, string? initialHide = null, string? initialFilter = null)
    {
        FilePath = filePath;
        Title = Path.GetFileName(filePath);
        _index = new LogFileIndex(filePath);
        _filtered = new FilteredRecordIndex(_index);

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        if (!string.IsNullOrEmpty(initialHighlight)) _highlightText = initialHighlight!;
        if (!string.IsNullOrEmpty(initialHide)) _hideText = initialHide!;
        if (!string.IsNullOrEmpty(initialFilter)) _filterText = initialFilter!;
        ApplyFilterState();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ApplyFilterState();
            Wake();
        };

        RetryCommand = new DelegateCommand(Retry, () => IsLocked);

        _ = Task.Run(() => BackgroundLoopAsync(_cts.Token));
    }

    public string FilePath { get; }

    public string Title { get; }

    public event EventHandler? DataChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DelegateCommand RetryCommand { get; }

    public string HighlightText
    {
        get => _highlightText;
        set
        {
            if (SetField(ref _highlightText, value))
            {
                RestartDebounce();
            }
        }
    }

    public string HideText
    {
        get => _hideText;
        set
        {
            if (SetField(ref _hideText, value))
            {
                RestartDebounce();
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value))
            {
                RestartDebounce();
            }
        }
    }

    public bool IsRawView
    {
        get => _isRawView;
        set => SetField(ref _isRawView, value);
    }

    public bool IsFollowing
    {
        get => _isFollowing;
        set => SetField(ref _isFollowing, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (SetField(ref _isLocked, value))
            {
                _dispatcher.BeginInvoke(() => RetryCommand.RaiseCanExecuteChanged());
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>Number of rows currently visible under the active Filter/Hide (or all records if none active).</summary>
    public long VisibleCount => _filtered.VisibleCount;

    public LogFormat Format => _index.Format;

    private void RestartDebounce()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void ApplyFilterState()
    {
        _filtered.State = new FilterState { Highlight = _highlightText, Hide = _hideText, Filter = _filterText };
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void Retry()
    {
        IsLocked = false;
        ErrorMessage = null;
        Wake();
    }

    /// <summary>Loads a contiguous window of rows for display, clamped to the currently known visible range.</summary>
    public IReadOnlyList<RecordRowViewModel> LoadWindow(long startIndex, int count)
    {
        long total = _filtered.VisibleCount;
        if (total <= 0 || count <= 0)
        {
            return Array.Empty<RecordRowViewModel>();
        }

        startIndex = Math.Clamp(startIndex, 0, Math.Max(0, total - 1));
        int clampedCount = (int)Math.Min(count, total - startIndex);
        if (clampedCount <= 0)
        {
            return Array.Empty<RecordRowViewModel>();
        }

        var records = _filtered.GetVisibleRange(startIndex, clampedCount);
        var state = new FilterState { Highlight = _highlightText, Hide = _hideText, Filter = _filterText };
        var rows = new List<RecordRowViewModel>(records.Count);
        foreach (var record in records)
        {
            var outcome = FilterEngine.Evaluate(record, state);
            rows.Add(RecordRowViewModel.Create(record, outcome.Highlighted));
        }

        return rows;
    }

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RefreshResult result;
            try
            {
                result = await Task.Run(() => _index.Refresh(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            switch (result)
            {
                case RefreshResult.Locked:
                    IsLocked = true;
                    ErrorMessage = $"\"{Path.GetFileName(FilePath)}\" is locked by another process (an exclusive writer). Click Retry to try again.";
                    break;
                case RefreshResult.Missing:
                    IsLocked = true;
                    ErrorMessage = $"\"{FilePath}\" no longer exists.";
                    break;
                case RefreshResult.Reset:
                    IsLocked = false;
                    ErrorMessage = null;
                    _filtered.OnUnderlyingIndexReset();
                    break;
                default:
                    IsLocked = false;
                    ErrorMessage = null;
                    break;
            }

            if (!IsLocked)
            {
                // Advance filter match-building in bounded batches so scanning the whole file for
                // matches never blocks the UI thread, and so matches far behind the tail eventually
                // become reachable even on multi-gigabyte files.
                while (!_filtered.IsFullyScanned && !ct.IsCancellationRequested)
                {
                    await Task.Run(() => _filtered.AdvanceMatchBuilding(MatchBuildBatchSize, ct), ct);
                }
            }

            UpdateStatusText();
            _lastKnownVisibleCount = _filtered.VisibleCount;
            RaiseDataChanged();

            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delayCts.CancelAfter(PollIntervalMs);
                await _wake.WaitAsync(delayCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Poll interval elapsed; loop again.
            }
        }
    }

    private void UpdateStatusText()
    {
        var encodingName = _index.Encoding.WebName.ToUpperInvariant();
        var formatName = _index.Format.ToString();
        var total = _index.TotalRecordCount;
        var visible = _filtered.VisibleCount;

        string filterPart = _filtered.IsFiltering
            ? _filtered.IsFullyScanned
                ? $" | {visible:N0} matching of {total:N0} rows"
                : $" | scanning… {_filtered.MatchCount:N0} matches so far ({_filtered.ScannedRecordCount:N0}/{total:N0} scanned)"
            : $" | {total:N0} rows";

        _dispatcher.Invoke(() => StatusText = $"{encodingName} · {formatName}{filterPart}");
    }

    private void RaiseDataChanged()
    {
        _dispatcher.BeginInvoke(() => DataChanged?.Invoke(this, EventArgs.Empty));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        if (_dispatcher.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        else
        {
            _dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _wake.Dispose();
        _debounceTimer.Stop();
    }
}
