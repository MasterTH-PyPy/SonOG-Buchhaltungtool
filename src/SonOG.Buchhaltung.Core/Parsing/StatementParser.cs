using System.Globalization;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Util;

namespace SonOG.Buchhaltung.Core.Parsing;

/// <summary>Spaltenlayout des Sparkassen-Kontoauszugs (Punkt, Ursprung links oben).</summary>
public sealed record StatementLayout(
    double DateXMax = 90,
    double TextXMin = 120,
    double AmountXMin = 480,
    double FooterY = 770,
    double LineTolerance = 2.5,
    /// <summary>Zellgrenze zwischen Datum und Erläuterung: Das PDF setzt beides ohne Leerzeichen direkt nebeneinander.</summary>
    double ColumnBreakX = 122);

/// <summary>
/// Liest die Buchungen aus den Wörtern eines Sparkassen-Kontoauszugs. Die Tabelle hat links das Datum,
/// daneben die Erläuterung (erste Zeile = Buchungsart, danach der Verwendungszweck) und rechts den Betrag.
/// </summary>
public static class StatementParser
{
    private static readonly Regex DateRx = new(@"^\d{2}\.\d{2}\.\d{4}$", RegexOptions.Compiled);
    private static readonly Regex AmountRx = new(@"^-?\d{1,3}(\.\d{3})*,\d{2}$", RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"Kontoauszug\s+(\d{1,2})/(\d{4})", RegexOptions.Compiled);
    private static readonly Regex OpeningRx = new(@"Kontostand am \d{2}\.\d{2}\.\d{4}, Auszug Nr\.\s*\d+\s+(-?[\d.]+,\d{2})", RegexOptions.Compiled);
    private static readonly Regex ClosingRx = new(@"Kontostand am \d{2}\.\d{2}\.\d{4} um \d{2}:\d{2} Uhr\s+(-?[\d.]+,\d{2})", RegexOptions.Compiled);

    public static ParsedStatement Parse(IReadOnlyList<PdfPageWords> pages, StatementLayout? layout = null)
    {
        layout ??= new StatementLayout();
        var bookings = new List<Booking>();
        foreach (var page in pages)
            bookings.AddRange(ParsePage(page, layout));

        string title = "";
        int year = 0, month = 0;
        decimal? opening = null, closing = null;

        foreach (var page in pages)
        {
            foreach (var line in TextLines.Group(page.Words, layout.LineTolerance))
            {
                if (title.Length == 0 && TitleRx.Match(line.Text) is { Success: true } t)
                {
                    month = int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                    year = int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture);
                    title = $"{month}/{year}";
                }
                if (opening is null && OpeningRx.Match(line.Text) is { Success: true } o)
                    opening = Fmt.ParseGerman(o.Groups[1].Value);
                if (closing is null && ClosingRx.Match(line.Text) is { Success: true } c)
                    closing = Fmt.ParseGerman(c.Groups[1].Value);
            }
        }

        if (year == 0 && bookings.Count > 0)
        {
            year = bookings[0].Date.Year;
            month = bookings[0].Date.Month;
            title = $"{month}/{year}";
        }

        return new ParsedStatement
        {
            Title = title,
            Year = year,
            Month = month,
            OpeningBalance = opening,
            ClosingBalance = closing,
            Bookings = bookings,
        };
    }

    private static IEnumerable<Booking> ParsePage(PdfPageWords page, StatementLayout lo)
    {
        var words = page.Words.Where(w => w.Y0 < lo.FooterY).ToList();

        // Zeilen "Kontostand ..." beenden die Buchungstabelle (Schlusssaldo).
        var stops = words.Where(w => w.Text == "Kontostand" && w.X0 < 130).Select(w => w.Y0).ToList();

        var dateWords = words
            .Where(w => w.X0 < lo.DateXMax && DateRx.IsMatch(w.Text))
            .OrderBy(w => w.Y0)
            .ToList();

        for (int i = 0; i < dateWords.Count; i++)
        {
            var dw = dateWords[i];
            double y0 = dw.Y0 - 1;
            double next = i + 1 < dateWords.Count ? dateWords[i + 1].Y0 - 1 : lo.FooterY;
            double y1 = next;
            foreach (var s in stops)
                if (s > dw.Y0 && s < y1) y1 = s;

            var block = words.Where(w => w.Y0 >= y0 && w.Y0 < y1 && w.X0 >= lo.TextXMin - 5).ToList();
            var firstLine = block.Where(w => Math.Abs(w.Y0 - dw.Y0) < 3).ToList();

            var amountWord = firstLine.LastOrDefault(w => w.X0 >= lo.AmountXMin && AmountRx.IsMatch(w.Text));
            if (amountWord is null) continue;

            var typeText = string.Join(" ", firstLine
                .Where(w => w.X0 < lo.AmountXMin && !ReferenceEquals(w, amountWord))
                .OrderBy(w => w.X0).Select(w => w.Text));

            var rest = block.Where(w => !firstLine.Contains(w) && w.X0 < lo.AmountXMin);
            var text = string.Join(" ", TextLines.Group(rest, lo.LineTolerance).Select(l => l.Text));

            yield return new Booking
            {
                PageIndex = page.PageIndex,
                Y0 = dw.Y0,
                Y1 = dw.Y1,
                Date = DateOnly.ParseExact(dw.Text, "dd.MM.yyyy", CultureInfo.InvariantCulture),
                Type = typeText,
                Amount = Fmt.ParseGerman(amountWord.Text),
                Text = text,
            };
        }
    }
}
