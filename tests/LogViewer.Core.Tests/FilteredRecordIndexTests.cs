using System.Text;
using LogViewer.Core.Filtering;
using LogViewer.Core.IO;
using Xunit;

namespace LogViewer.Core.Tests;

public class FilteredRecordIndexTests : IDisposable
{
    private readonly string _tempDir;

    public FilteredRecordIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LogViewerTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(int lineCount, Func<int, string> lineFor)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid() + ".log");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        for (int i = 0; i < lineCount; i++)
        {
            writer.WriteLine(lineFor(i));
        }

        return path;
    }

    [Fact]
    public void No_filter_is_a_direct_passthrough_over_all_records()
    {
        var path = WriteFile(10, i => $"line {i}");
        var index = new LogFileIndex(path);
        index.Refresh();

        var filtered = new FilteredRecordIndex(index);
        Assert.False(filtered.IsFiltering);
        Assert.Equal(10, filtered.VisibleCount);
        Assert.Equal("line 5", filtered.GetVisible(5)!.RawText);
    }

    [Fact]
    public void Filter_finds_matches_anywhere_in_the_file_not_just_the_tail()
    {
        // Only line 9999 (near the start) contains the needle; everything else in between does not.
        var path = WriteFile(20_000, i => i == 42 ? "needle here" : $"filler {i}");
        var index = new LogFileIndex(path);
        index.Refresh();

        var filtered = new FilteredRecordIndex(index) { State = new FilterState { Filter = "needle" } };
        filtered.AdvanceMatchBuilding(int.MaxValue);

        Assert.True(filtered.IsFullyScanned);
        Assert.Equal(1, filtered.VisibleCount);
        Assert.Equal("needle here", filtered.GetVisible(0)!.RawText);
    }

    [Fact]
    public void Match_building_can_be_advanced_incrementally_in_bounded_batches()
    {
        var path = WriteFile(1000, i => i % 10 == 0 ? $"match {i}" : $"other {i}");
        var index = new LogFileIndex(path);
        index.Refresh();

        var filtered = new FilteredRecordIndex(index) { State = new FilterState { Filter = "match" } };

        int iterations = 0;
        while (!filtered.IsFullyScanned)
        {
            filtered.AdvanceMatchBuilding(37); // deliberately not a divisor of 1000
            iterations++;
            Assert.True(iterations < 1000, "should make bounded forward progress each call");
        }

        Assert.Equal(100, filtered.VisibleCount);
        Assert.True(iterations > 1, "batches should require more than one call for a 1000-line file");
    }

    [Fact]
    public void Hide_and_filter_combine_and_matches_are_reset_when_filter_state_changes()
    {
        var path = WriteFile(50, i => i % 2 == 0 ? $"even {i}" : $"odd {i}");
        var index = new LogFileIndex(path);
        index.Refresh();

        var filtered = new FilteredRecordIndex(index) { State = new FilterState { Filter = "e", Hide = "even" } };
        filtered.AdvanceMatchBuilding(int.MaxValue);
        Assert.All(Enumerable.Range(0, (int)filtered.VisibleCount), i => Assert.Contains("e", filtered.GetVisible(i)!.RawText));

        // Changing the filter must discard stale matches.
        filtered.State = new FilterState { Filter = "odd" };
        Assert.Equal(0, filtered.VisibleCount);
        filtered.AdvanceMatchBuilding(int.MaxValue);
        Assert.Equal(25, filtered.VisibleCount);
    }
}
