using LogViewer.Core.Cli;
using Xunit;

namespace LogViewer.Core.Tests;

public class CommandLineArgsTests
{
    [Fact]
    public void Parses_single_file_and_filters()
    {
        var result = CommandLineArgs.Parse(new[] { "-File", "C:\\logs\\a.log", "-Highlight", "error", "-Hide", "debug", "-Filter", "download" });

        Assert.Single(result.Files);
        Assert.Equal("C:\\logs\\a.log", result.Files[0]);
        Assert.Equal("error", result.Highlight);
        Assert.Equal("debug", result.Hide);
        Assert.Equal("download", result.Filter);
        Assert.False(result.ShowHelp);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Supports_multiple_file_switches()
    {
        var result = CommandLineArgs.Parse(new[] { "-File", "a.log", "-File", "b.log", "-File", "c.log" });
        Assert.Equal(new[] { "a.log", "b.log", "c.log" }, result.Files);
    }

    [Fact]
    public void Bare_path_without_switch_is_treated_as_file()
    {
        var result = CommandLineArgs.Parse(new[] { "C:\\logs\\a.log" });
        Assert.Single(result.Files);
        Assert.Equal("C:\\logs\\a.log", result.Files[0]);
    }

    [Theory]
    [InlineData("-help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Recognizes_all_help_spellings(string token)
    {
        var result = CommandLineArgs.Parse(new[] { token });
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void Missing_value_reports_error_instead_of_throwing()
    {
        var result = CommandLineArgs.Parse(new[] { "-Filter" });
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Quoted_values_with_spaces_are_preserved_as_single_tokens()
    {
        // The shell/CLR already splits quoted args into single tokens; simulate that here.
        var result = CommandLineArgs.Parse(new[] { "-Filter", "connection error" });
        Assert.Equal("connection error", result.Filter);
    }

    [Fact]
    public void Last_value_wins_when_switch_repeated()
    {
        var result = CommandLineArgs.Parse(new[] { "-Highlight", "first", "-Highlight", "second" });
        Assert.Equal("second", result.Highlight);
    }
}
