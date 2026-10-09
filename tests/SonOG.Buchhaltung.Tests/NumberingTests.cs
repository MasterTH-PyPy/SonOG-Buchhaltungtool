using SonOG.Buchhaltung.Core.Numbering;

namespace SonOG.Buchhaltung.Tests;

public class NumberingTests
{
    [Theory]
    [InlineData("{year}-{n:0000}", 2026, 7, "2026-0007")]
    [InlineData("{year}-{n:0000}", 2026, 12345, "2026-12345")]
    [InlineData("B{n}", 2026, 42, "B42")]
    [InlineData("{year}/{n:000}", 2027, 5, "2027/005")]
    public void Format_replaces_placeholders(string pattern, int year, int n, string expected)
        => Assert.Equal(expected, NumberFormat.Format(pattern, year, n));

    [Fact]
    public void Numbers_continue_across_statements()
    {
        var s = new NumberingState();
        Assert.Equal(1, s.Commit(2026, "Auszug_08", 50));
        Assert.Equal(51, s.Commit(2026, "Auszug_09", 87));
        Assert.Equal(138, s.PeekStart(2026, "Auszug_10"));
    }

    [Fact]
    public void Same_statement_gets_same_numbers_again()
    {
        var s = new NumberingState();
        s.Commit(2026, "Auszug_08", 50);
        var first = s.Commit(2026, "Auszug_09", 87);
        Assert.Equal(first, s.Commit(2026, "Auszug_09", 87));
        Assert.Equal(first, s.PeekStart(2026, "Auszug_09"));
    }

    [Fact]
    public void Changed_booking_count_is_refused()
    {
        var s = new NumberingState();
        s.Commit(2026, "Auszug_09", 87);
        Assert.Throws<InvalidOperationException>(() => s.Commit(2026, "Auszug_09", 88));
    }

    [Fact]
    public void New_year_starts_at_one()
    {
        var s = new NumberingState();
        s.Commit(2026, "Auszug_12", 10);
        Assert.Equal(1, s.Commit(2027, "Auszug_01", 5));
    }

    [Fact]
    public void Release_of_last_statement_resets_counter_and_paid_entries()
    {
        var s = new NumberingState();
        s.Commit(2026, "A", 10);
        s.Commit(2026, "B", 20);
        s.MarkPaid("202634000", "2026-0015", "B");
        Assert.True(s.Release(2026, "B"));
        Assert.Equal(11, s.PeekStart(2026, "B"));
        Assert.Empty(s.PaidExcluding("other"));
    }

    [Fact]
    public void Paid_invoices_of_current_statement_are_excluded()
    {
        var s = new NumberingState();
        s.MarkPaid("202634000", "2026-0001", "A");
        s.MarkPaid("202634001", "2026-0002", "B");
        var paid = s.PaidExcluding("B");
        Assert.True(paid.ContainsKey("202634000"));
        Assert.False(paid.ContainsKey("202634001"));
    }

    [Fact]
    public void State_survives_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "sonog-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new NumberingState();
            s.Commit(2026, "A", 10);
            s.MarkPaid("202634000", "2026-0003", "A");
            s.Save(path);

            var loaded = NumberingState.Load(path);
            Assert.Equal(11, loaded.PeekStart(2026, "B"));
            Assert.Equal("2026-0003", loaded.PaidInvoices["202634000"].Booking);
        }
        finally { File.Delete(path); }
    }
}
