using System.ComponentModel;
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

        RowList.ItemsSource = _viewModel.LoadWindow(_topIndex, _windowSize);
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
}
