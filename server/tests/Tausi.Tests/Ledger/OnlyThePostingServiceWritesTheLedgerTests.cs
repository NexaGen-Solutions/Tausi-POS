using FluentAssertions;

namespace Tausi.Tests.Ledger;

/// <summary>AGENTS.md rule 6: no code but ILedgerPostingService writes journal entries.</summary>
public class OnlyThePostingServiceWritesTheLedgerTests
{
    [Fact]
    public void No_source_file_but_the_posting_service_adds_journal_entries_or_lines()
    {
        var src = Source();
        var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains("JournalEntries.Add") || text.Contains("JournalLines.Add") || text.Contains("new JournalEntry\n")
                            || text.Contains("new JournalEntry\r\n") || text.Contains("new JournalEntry {") || text.Contains("new JournalLine {")
                            || text.Contains("new JournalLine\n") || text.Contains("new JournalLine\r\n")))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        writers.Should().Equal(["Tausi.Infrastructure/Ledger/LedgerPostingService.cs"]);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Tausi.Infrastructure"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("server/src not found"), "src");
    }
}
