using AdminConsole.Infrastructure.Telegram;

namespace AdminConsole.Tests.Telegram;

public sealed class TelegramTextChunkerTests
{
    [Fact]
    public void BuildPages_ShortLines_ReturnsSinglePage()
    {
        var pages = TelegramTextChunker.BuildPages(["line one", "line two"], header: "Header");

        var page = Assert.Single(pages);
        Assert.StartsWith("Header", page);
        Assert.Contains("line one", page);
        Assert.Contains("line two", page);
    }

    [Fact]
    public void BuildPages_LineLongerThanMaxChars_IsTruncatedWithEllipsis_NeverSplitAcrossPages()
    {
        var longLine = new string('x', 50);

        var pages = TelegramTextChunker.BuildPages([longLine], header: "", maxChars: 20);

        var page = Assert.Single(pages);
        Assert.EndsWith("…", page.TrimEnd());
        Assert.True(page.Length <= 20);
    }

    [Fact]
    public void BuildPages_ManyLines_SplitsIntoMultiplePages_EachWithinMaxChars()
    {
        // Each line is 10 chars + '\n' = 11; maxChars=25 fits at most 2 lines
        // per page before the 3rd would overflow.
        var lines = Enumerable.Range(0, 6).Select(i => $"line{i:00}xx").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "", maxChars: 25);

        Assert.True(pages.Count > 1);
        foreach (var page in pages)
            Assert.True(page.Length <= 25, $"Page exceeded maxChars: '{page}' ({page.Length} chars)");

        // No line was dropped — every original line appears somewhere across the pages.
        foreach (var line in lines)
            Assert.Contains(pages, p => p.Contains(line));
    }

    [Fact]
    public void BuildPages_EveryPage_RepeatsTheHeader()
    {
        var lines = Enumerable.Range(0, 6).Select(i => $"line{i:00}xx").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "PAGE", maxChars: 25);

        Assert.True(pages.Count > 1);
        foreach (var page in pages)
            Assert.StartsWith("PAGE", page);
    }

    [Fact]
    public void BuildPages_NoLines_ReturnsHeaderOnlyPage()
    {
        var pages = TelegramTextChunker.BuildPages([], header: "Nothing to report");

        var page = Assert.Single(pages);
        Assert.Equal("Nothing to report", page);
    }
}
