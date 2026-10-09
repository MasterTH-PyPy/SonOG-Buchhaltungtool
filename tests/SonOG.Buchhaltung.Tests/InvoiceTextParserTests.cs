using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Tests;

public class InvoiceTextParserTests
{
    private static PdfPageWords Page(params (double x, double y, string text)[] lines)
    {
        var words = new List<PdfWord>();
        foreach (var (x, y, text) in lines) words.AddRange(Fx.Line(x, y, text));
        return new PdfPageWords(0, 595, 842, words);
    }

    [Fact]
    public void Reads_recipient_total_and_date()
    {
        var page = Page(
            (350, 60, "Rechnung Nr. 202634552"),
            (350, 80, "Rechnungsdatum 08.09.2026"),
            (70, 140, "Erika Beispiel"),
            (70, 152, "Musterweg 1"),
            (70, 164, "64319 Musterstadt"),
            (70, 600, "Summe netto 1.037,45"),
            (70, 612, "MwSt 19 % 197,11"),
            (70, 630, "Gesamtbetrag EUR 1.234,56"));

        var c = InvoiceTextParser.Parse(new[] { page }, new InvoiceReadRules());

        Assert.Equal(1234.56m, c.Gross);
        Assert.Equal("Erika Beispiel\nMusterweg 1\n64319 Musterstadt", c.Recipient);
        Assert.Equal(new DateOnly(2026, 9, 8), c.Date);
    }

    [Fact]
    public void Total_is_taken_from_last_page()
    {
        var p1 = Page((70, 140, "Erika Beispiel"), (70, 700, "Zwischensumme Brutto 100,00"));
        var p2 = new PdfPageWords(1, 595, 842, Fx.Line(70, 300, "Gesamtbetrag 250,00").ToList());
        Assert.Equal(250.00m, InvoiceTextParser.Parse(new[] { p1, p2 }, new InvoiceReadRules()).Gross);
    }

    [Fact]
    public void Missing_total_gives_null()
    {
        var page = Page((70, 140, "Erika Beispiel"), (70, 600, "Positionen 12,00"));
        Assert.Null(InvoiceTextParser.Parse(new[] { page }, new InvoiceReadRules()).Gross);
    }

    [Fact]
    public void Recipient_falls_back_to_top_of_page_when_region_is_empty()
    {
        var page = Page((400, 20, "Erika Beispiel"), (400, 32, "Musterweg 1"));
        Assert.Contains("Erika Beispiel", InvoiceTextParser.Parse(new[] { page }, new InvoiceReadRules()).Recipient);
    }

    [Theory]
    [InlineData("CAO-Faktura Rechnung Nr._202634596.pdf", "202634596")]
    [InlineData("Beispiel Kunde + Rechnung 202634596.pdf", "202634596")]
    [InlineData("Rechnung.pdf", null)]
    public void Invoice_number_from_file_name(string file, string? expected)
        => Assert.Equal(expected, InvoiceFileName.TryGetNumber(file, new Regex(new AppRules().EigeneRechnungsnummerRegex)));
}
