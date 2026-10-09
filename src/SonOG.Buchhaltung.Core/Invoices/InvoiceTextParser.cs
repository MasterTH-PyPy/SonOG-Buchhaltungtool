using System.Globalization;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Parsing;
using SonOG.Buchhaltung.Core.Rules;
using SonOG.Buchhaltung.Core.Util;

namespace SonOG.Buchhaltung.Core.Invoices;

public sealed record ParsedInvoiceContent(decimal? Gross, string Recipient, DateOnly? Date);

public static class InvoiceFileName
{
    /// <summary>Rechnungsnummer aus dem Dateinamen, z. B. "CAO-Faktura Rechnung Nr._202634596.pdf" -> 202634596.</summary>
    public static string? TryGetNumber(string fileName, Regex ownNumberRegex)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = ownNumberRegex.Match(stem);
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// <summary>
/// Liest Rechnungsbetrag, Empfänger und Datum aus den Wörtern einer Rechnung. Die Suche ist über
/// <see cref="InvoiceReadRules"/> (regeln.json) an das Layout der CAO-Rechnungen anpassbar.
/// </summary>
public static class InvoiceTextParser
{
    private static readonly Regex AmountRx = new(@"(?<![\d.,])-?\d{1,3}(?:\.\d{3})*,\d{2}(?!\d)", RegexOptions.Compiled);

    public static ParsedInvoiceContent Parse(IReadOnlyList<PdfPageWords> pages, InvoiceReadRules rules)
    {
        if (pages.Count == 0) return new ParsedInvoiceContent(null, "", null);

        return new ParsedInvoiceContent(ReadGross(pages, rules), ReadRecipient(pages[0], rules), ReadDate(pages[0], rules));
    }

    private static decimal? ReadGross(IReadOnlyList<PdfPageWords> pages, InvoiceReadRules rules)
    {
        // Von unten nach oben: die letzte Zeile mit einem Summen-Stichwort ist der Rechnungsendbetrag.
        for (int p = pages.Count - 1; p >= 0; p--)
        {
            var lines = TextLines.Group(pages[p].Words);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var text = lines[i].Text;
                if (!rules.BetragStichwoerter.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                var amounts = AmountRx.Matches(text);
                if (amounts.Count > 0) return Fmt.ParseGerman(amounts[^1].Value);
            }
        }
        return null;
    }

    private static string ReadRecipient(PdfPageWords first, InvoiceReadRules rules)
    {
        var r = rules.Empfaenger;
        var inRegion = first.Words.Where(w => w.X0 >= r.X0 - 2 && w.X0 <= r.X1 && w.Y0 >= r.Y0 && w.Y0 <= r.Y1);
        var lines = TextLines.Group(inRegion).Select(l => l.Text).Where(t => t.Length > 0).ToList();

        if (lines.Count == 0)
            lines = TextLines.Group(first.Words).Take(12).Select(l => l.Text).ToList();

        return string.Join("\n", lines);
    }

    private static DateOnly? ReadDate(PdfPageWords first, InvoiceReadRules rules)
    {
        var rx = new Regex(rules.DatumRegex);
        var lines = TextLines.Group(first.Words);

        foreach (var line in lines.Where(l => l.Text.Contains("datum", StringComparison.OrdinalIgnoreCase)))
            if (TryDate(rx, line.Text, out var d)) return d;
        foreach (var line in lines)
            if (TryDate(rx, line.Text, out var d)) return d;
        return null;
    }

    private static bool TryDate(Regex rx, string text, out DateOnly date)
    {
        var m = rx.Match(text);
        if (m.Success && DateOnly.TryParseExact(m.Groups[1].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        date = default;
        return false;
    }
}
