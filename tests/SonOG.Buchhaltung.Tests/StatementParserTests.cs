using SonOG.Buchhaltung.Core.Parsing;

namespace SonOG.Buchhaltung.Tests;

public class StatementParserTests
{
    [Fact]
    public void Reads_bookings_with_amount_type_and_multiline_text()
    {
        var page = Fx.StatementPage(0, new[]
        {
            new Fx.Row("01.09.2026", "Lastschrift", "-440,30", "Musterfirma GmbH Mobilfunk RG 4711", "Zahlbeleg 12345"),
            new Fx.Row("02.09.2026", "GutschriftÜberweisung", "1.099,00", "Max Mustermann Rechnungsnummer: 202634000"),
        }, opening: "33.076,36", closing: "33.735,06");

        var st = StatementParser.Parse(new[] { page });

        Assert.Equal(2, st.Bookings.Count);
        Assert.Equal(-440.30m, st.Bookings[0].Amount);
        Assert.Equal("Lastschrift", st.Bookings[0].Type);
        Assert.Equal("Musterfirma GmbH Mobilfunk RG 4711 Zahlbeleg 12345", st.Bookings[0].Text);
        Assert.Equal(new DateOnly(2026, 9, 1), st.Bookings[0].Date);
        Assert.Equal(1099.00m, st.Bookings[1].Amount);
        Assert.Equal("Max Mustermann Rechnungsnummer: 202634000", st.Bookings[1].Text);
        Assert.Equal(2026, st.Year);
        Assert.Equal("9/2026", st.Title);
    }

    [Fact]
    public void Balance_check_is_ok_when_sum_matches()
    {
        var page = Fx.StatementPage(0, new[]
        {
            new Fx.Row("01.09.2026", "Lastschrift", "-440,30", "A"),
            new Fx.Row("02.09.2026", "GutschriftÜberweisung", "1.099,00", "B"),
        }, opening: "1.000,00", closing: "1.658,70");

        var st = StatementParser.Parse(new[] { page });

        Assert.Equal(1000.00m, st.OpeningBalance);
        Assert.Equal(1658.70m, st.ClosingBalance);
        Assert.True(st.BalanceOk);
    }

    [Fact]
    public void Balance_check_detects_missing_booking()
    {
        var page = Fx.StatementPage(0, new[] { new Fx.Row("01.09.2026", "Lastschrift", "-440,30", "A") },
            opening: "1.000,00", closing: "1.658,70");

        Assert.False(StatementParser.Parse(new[] { page }).BalanceOk);
    }

    [Fact]
    public void Balance_check_is_null_without_balances()
    {
        var page = Fx.StatementPage(0, new[] { new Fx.Row("01.09.2026", "Lastschrift", "-1,00", "A") });
        Assert.Null(StatementParser.Parse(new[] { page }).BalanceOk);
    }

    [Fact]
    public void Reads_bookings_across_pages_and_ignores_footer()
    {
        var p1 = Fx.StatementPage(0, new[] { new Fx.Row("01.09.2026", "Lastschrift", "-10,00", "A") }, opening: "100,00");
        var p2 = Fx.StatementPage(1, new[] { new Fx.Row("02.09.2026", "Lastschrift", "-20,00", "B") }, closing: "70,00");

        var st = StatementParser.Parse(new[] { p1, p2 });

        Assert.Equal(2, st.Bookings.Count);
        Assert.Equal(0, st.Bookings[0].PageIndex);
        Assert.Equal(1, st.Bookings[1].PageIndex);
        Assert.True(st.BalanceOk);
    }
}
