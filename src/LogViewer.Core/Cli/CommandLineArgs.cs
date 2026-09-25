namespace LogViewer.Core.Cli;

/// <summary>Result of parsing the process command line.</summary>
public sealed class ParsedArgs
{
    public List<string> Files { get; } = new();
    public string? Highlight { get; set; }
    public string? Hide { get; set; }
    public string? Filter { get; set; }
    public bool ShowHelp { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Parses LogViewer's command line.
///
/// Usage:
///   LogViewer.App.exe [-File &lt;path&gt; [-File &lt;path&gt; ...]] [-Highlight &lt;text&gt;] [-Hide &lt;text&gt;] [-Filter &lt;text&gt;]
///   LogViewer.App.exe --help
///
/// Semantics:
///  - Each "-File &lt;path&gt;" opens one additional tab. Repeat -File to open several files at once,
///    e.g.: -File "C:\logs\a.log" -File "C:\logs\b.log"
///  - A bare path with no preceding switch is also treated as a file to open, so
///    "LogViewer.App.exe C:\logs\a.log" works too (useful for shell "Open with" registration).
///  - -Highlight / -Hide / -Filter each take exactly one text value and are applied, as the initial
///    per-tab filter state, to every tab opened from this command line (not to tabs opened later by
///    the user via drag/drop or the Open dialog). If given more than once, the last value wins.
///  - Matching is a simple case-insensitive substring match; wrap values containing spaces in quotes,
///    e.g.: -Filter "connection error"
///  - -Help, --help, -h and /? print usage and exit without opening any window.
///
/// Examples:
///   LogViewer.App.exe -File "C:\Windows\CCM\Logs\ccmexec.log" -Highlight error
///   LogViewer.App.exe -File a.log -File b.log -Filter "download" -Hide "heartbeat"
/// </summary>
public static class CommandLineArgs
{
    public const string HelpText =
        """
        LogViewer - Windows log viewer

        Usage:
          LogViewer.App.exe [-File <path> [-File <path> ...]] [-Highlight <text>] [-Hide <text>] [-Filter <text>]
          LogViewer.App.exe --help

        Options:
          -File <path>       Open <path> in a new tab. Repeatable for multiple files.
                              A bare path with no switch is treated the same way.
          -Highlight <text>  Highlight rows containing <text> (case-insensitive substring).
          -Hide <text>       Hide rows containing <text> (case-insensitive substring).
          -Filter <text>     Show only rows containing <text> (case-insensitive substring).
          -Help, -h, /?      Show this help and exit.

        Notes:
          - -Highlight/-Hide/-Filter apply to every file opened from this command line.
          - Quote values containing spaces, e.g. -Filter "connection error".

        Examples:
          LogViewer.App.exe -File "C:\Windows\CCM\Logs\ccmexec.log" -Highlight error
          LogViewer.App.exe -File a.log -File b.log -Filter download -Hide heartbeat
        """;

    public static ParsedArgs Parse(string[] args)
    {
        var result = new ParsedArgs();

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var lower = token.ToLowerInvariant();

            switch (lower)
            {
                case "-help":
                case "--help":
                case "-h":
                case "/?":
                    result.ShowHelp = true;
                    break;

                case "-file":
                case "--file":
                    if (!TryTakeValue(args, ref i, out var file))
                    {
                        result.Errors.Add($"Missing value for {token}.");
                    }
                    else
                    {
                        result.Files.Add(file);
                    }

                    break;

                case "-highlight":
                case "--highlight":
                    if (!TryTakeValue(args, ref i, out var highlight))
                    {
                        result.Errors.Add($"Missing value for {token}.");
                    }
                    else
                    {
                        result.Highlight = highlight;
                    }

                    break;

                case "-hide":
                case "--hide":
                    if (!TryTakeValue(args, ref i, out var hide))
                    {
                        result.Errors.Add($"Missing value for {token}.");
                    }
                    else
                    {
                        result.Hide = hide;
                    }

                    break;

                case "-filter":
                case "--filter":
                    if (!TryTakeValue(args, ref i, out var filter))
                    {
                        result.Errors.Add($"Missing value for {token}.");
                    }
                    else
                    {
                        result.Filter = filter;
                    }

                    break;

                default:
                    // A bare token (no recognized switch) is treated as a file path.
                    result.Files.Add(token);
                    break;
            }
        }

        return result;
    }

    private static bool TryTakeValue(string[] args, ref int i, out string value)
    {
        if (i + 1 < args.Length)
        {
            i++;
            value = args[i];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
