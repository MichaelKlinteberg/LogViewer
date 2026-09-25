using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LogViewer.App.ViewModels;

namespace LogViewer.App.Views;

/// <summary>
/// Displays one log tab. Implements bounded-memory scrolling itself (rather than relying on WPF's
/// built-in list virtualization) via a custom vertical <see cref="ScrollBar"/> sized to the full
/// (possibly filtered) row count: only the current on-screen window of rows is ever materialized in
/// <see cref="RowList"/>'s ItemsSource, so display memory does not grow with file size.
/// </summary>
public partial class LogTabView : UserControl
{
    private const int RowBufferAbove = 20;
    private const double EstimatedRowHeight = 20d;

    private LogTabViewModel? _viewModel;
    private long _topIndex;
    private int _windowSize = 50;
    private bool _isProgrammaticScrollChange;

    public LogTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is LogTabViewModel vm)
        {
            _viewModel = vm;
            vm.DataChanged += OnDataChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyViewMode();
            RecomputeWindowSizeAndReload();
        }
    }

    private void Detach()
    {
        if (_viewModel != null)
        {
            _viewModel.DataChanged -= OnDataChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogTabViewModel.IsRawView))
        {
            Dispatcher.Invoke(ApplyViewMode);
        }
        else if (e.PropertyName == nameof(LogTabViewModel.IsFollowing) && _viewModel?.IsFollowing == true)
        {
            Dispatcher.Invoke(ScrollToTail);
        }
    }

    private void ApplyViewMode()
    {
        if (_viewModel == null)
        {
            return;
        }

        RowList.View = (System.Windows.Controls.ViewBase)(_viewModel.IsRawView ? FindResource("RawGridView") : FindResource("ParsedGridView"));
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_viewModel.IsFollowing)
            {
                ScrollToTail();
            }
            else
            {
                UpdateScrollBarRange();
                ReloadWindow();
            }
        });
    }

    private void RowList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecomputeWindowSizeAndReload();
    }

    private void RecomputeWindowSizeAndReload()
    {
        int visibleRows = Math.Max(10, (int)(RowList.ActualHeight / EstimatedRowHeight));
        _windowSize = visibleRows + RowBufferAbove;

        if (_viewModel?.IsFollowing == true)
        {
            ScrollToTail();
        }
        else
        {
            UpdateScrollBarRange();
            ReloadWindow();
        }
    }

    private void ScrollToTail()
    {
        if (_viewModel == null)
        {
            return;
        }

        long total = _viewModel.VisibleCount;
        _topIndex = Math.Max(0, total - _windowSize);
        UpdateScrollBarRange();
        ReloadWindow();
    }

    private void UpdateScrollBarRange()
    {
        if (_viewModel == null)
        {
            return;
        }

        long total = _viewModel.VisibleCount;
        _isProgrammaticScrollChange = true;
        try
        {
            VirtualScrollBar.Maximum = Math.Max(0, total - _windowSize);
            VirtualScrollBar.ViewportSize = _windowSize;
            VirtualScrollBar.LargeChange = Math.Max(1, _windowSize);
            VirtualScrollBar.SmallChange = 1;
            _topIndex = (long)Math.Clamp(_topIndex, 0, VirtualScrollBar.Maximum);
            VirtualScrollBar.Value = _topIndex;
        }
        finally
        {
            _isProgrammaticScrollChange = false;
        }
    }

    private void ReloadWindow()
    {
        if (_viewModel == null)
        {
            return;
        }

        // Preserve the user's row selection across window reloads (which happen on every background
        // poll tick, even while paused/Follow is off, since ingestion never stops). Without this, a
        // multi-select-then-Ctrl+C copy of older content could be wiped out by an in-flight reload
        // triggered by unrelated new data arriving at the tail of the file.
        var previousSelection = RowList.SelectedItems.OfType<RecordRowViewModel>()
            .Select(r => r.Index)
            .ToHashSet();

        var rows = _viewModel.LoadWindow(_topIndex, _windowSize);
        RowList.ItemsSource = rows;

        if (previousSelection.Count > 0)
        {
            foreach (var row in rows)
            {
                if (previousSelection.Contains(row.Index))
                {
                    RowList.SelectedItems.Add(row);
                }
            }
        }
    }

    private void VirtualScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _topIndex = (long)e.NewValue;

        if (!_isProgrammaticScrollChange && _viewModel != null)
        {
            // A manual scroll away from the tail pauses following so the user can read older content
            // without ingestion (or the follow toggle) being affected.
            _viewModel.IsFollowing = false;
        }

        ReloadWindow();
    }

    private void RowList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int delta = e.Delta > 0 ? -3 : 3;
        double newValue = Math.Clamp(VirtualScrollBar.Value + delta, VirtualScrollBar.Minimum, VirtualScrollBar.Maximum);
        VirtualScrollBar.Value = newValue;
    }

    private void SelectAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        RowList.SelectAll();
    }

    private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = RowList.SelectedItems.Count > 0;
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        string text = RowCopyHelper.BuildClipboardText(RowList.SelectedItems.OfType<RecordRowViewModel>());
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process briefly held the clipboard open; silently ignore, matching typical
            // Windows app behavior for transient clipboard-lock failures on Ctrl+C.
        }
    }
}
