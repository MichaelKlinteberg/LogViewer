using System.Runtime.InteropServices;
using System.Windows;
using LogViewer.Core.Cli;

namespace LogViewer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var parsed = CommandLineArgs.Parse(e.Args);

        if (parsed.ShowHelp)
        {
            PrintHelpAndExit();
            return;
        }

        if (parsed.Errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, parsed.Errors) + Environment.NewLine + Environment.NewLine + "Run with --help for usage.",
                "LogViewer - command line error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (parsed.Files.Count > 0)
        {
            mainWindow.OpenFiles(parsed.Files, parsed.Highlight, parsed.Hide, parsed.Filter);
        }
    }

    private void PrintHelpAndExit()
    {
        if (AttachConsole(AttachParentProcess))
        {
            Console.WriteLine();
            Console.WriteLine(CommandLineArgs.HelpText);
        }
        else
        {
            MessageBox.Show(CommandLineArgs.HelpText, "LogViewer - help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Shutdown(0);
    }
}

