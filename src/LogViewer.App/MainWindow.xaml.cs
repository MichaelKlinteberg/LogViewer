using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LogViewer.App.ViewModels;
using LogViewer.App.Views;
using Microsoft.Win32;
namespace LogViewer.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. Hosts one tab per open file; accepts files dropped from
/// Windows File Explorer (possibly several at once) as well as the Open file dialog and command-line
/// arguments.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => ShowOpenDialog()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
        Closing += (_, _) => CloseAllTabs();
    }

    /// <summary>Opens each path in its own new tab, applying the given filter state (typically from the command line) to all of them.</summary>
    public void OpenFiles(IEnumerable<string> paths, string? highlight = null, string? hide = null, string? filter = null)
    {
        foreach (var path in paths)
        {
            OpenFile(path, highlight, hide, filter);
        }
    }

    private void OpenFile(string path, string? highlight, string? hide, string? filter)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"File not found:\n{path}", "LogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var viewModel = new LogTabViewModel(path, highlight, hide, filter);
        var view = new LogTabView { DataContext = viewModel };
        var tabItem = new TabItem { Header = viewModel.Title, Content = view, ToolTip = viewModel.FilePath };

        Tabs.Items.Add(tabItem);
        Tabs.SelectedItem = tabItem;
    }

    private void ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Open log file(s)",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenFiles(dialog.FileNames);
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            OpenFiles(paths);
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tabItem })
        {
            if (tabItem.Content is LogTabView { DataContext: LogTabViewModel vm })
            {
                vm.Dispose();
            }

            Tabs.Items.Remove(tabItem);
        }
    }

    private void CloseAllTabs()
    {
        foreach (var item in Tabs.Items)
        {
            if (item is TabItem { Content: LogTabView { DataContext: LogTabViewModel vm } })
            {
                vm.Dispose();
            }
        }
    }
}