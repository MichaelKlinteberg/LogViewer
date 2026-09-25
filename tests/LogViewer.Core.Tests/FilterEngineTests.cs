using LogViewer.Core.Filtering;
using LogViewer.Core.Records;
using Xunit;

namespace LogViewer.Core.Tests;

public class FilterEngineTests
{
    private static LogRecord Record(string text) => new()
    {
        Index = 0,
        ByteOffset = 0,
        ByteLength = text.Length,
        RawText = text,
    };

    [Theory]
    [InlineData("bla bla banan", "banan", true)]
    [InlineData("bla bla Banan bla bla", "banan", true)]
    [InlineData("bla bla BANAN", "banan", true)]
    [InlineData("nothing here", "banan", false)]
    public void Filter_is_case_insensitive_substring(string text, string needle, bool expectedVisible)
    {
        var state = new FilterState { Filter = needle };
        var outcome = FilterEngine.Evaluate(Record(text), state);
        Assert.Equal(expectedVisible, outcome.Visible);
    }

    [Fact]
    public void Empty_filters_disable_respective_behavior()
    {
        var state = new FilterState();
        var outcome = FilterEngine.Evaluate(Record("anything at all"), state);
        Assert.True(outcome.Visible);
        Assert.False(outcome.Highlighted);
    }

    [Fact]
    public void Hide_removes_rows_containing_its_string()
    {
        var state = new FilterState { Hide = "debug" };
        Assert.False(FilterEngine.Evaluate(Record("this is a Debug message"), state).Visible);
        Assert.True(FilterEngine.Evaluate(Record("this is an info message"), state).Visible);
    }

    [Fact]
    public void Filter_then_hide_then_highlight_combine_logically()
    {
        // Filter requires "error"; Hide removes "ignore"; Highlight marks "critical".
        var state = new FilterState { Filter = "error", Hide = "ignore", Highlight = "critical" };

        var passesAll = FilterEngine.Evaluate(Record("critical error occurred"), state);
        Assert.True(passesAll.Visible);
        Assert.True(passesAll.Highlighted);

        var failsFilter = FilterEngine.Evaluate(Record("critical warning occurred"), state);
        Assert.False(failsFilter.Visible);

        var hiddenDespiteFilterMatch = FilterEngine.Evaluate(Record("error but please ignore this one"), state);
        Assert.False(hiddenDespiteFilterMatch.Visible);
        Assert.False(hiddenDespiteFilterMatch.Highlighted);

        var visibleNotHighlighted = FilterEngine.Evaluate(Record("error happened again"), state);
        Assert.True(visibleNotHighlighted.Visible);
        Assert.False(visibleNotHighlighted.Highlighted);
    }

    [Fact]
    public void Highlight_never_affects_visibility_on_its_own()
    {
        var state = new FilterState { Highlight = "warn" };
        var outcome = FilterEngine.Evaluate(Record("no match here"), state);
        Assert.True(outcome.Visible);
        Assert.False(outcome.Highlighted);
    }
}
