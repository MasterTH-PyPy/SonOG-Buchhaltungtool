using SonOG.Buchhaltung.Core.Classification;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Tests;

/// <summary>Erzeugt synthetische Seiten im Layout eines Sparkassen-Kontoauszugs (keine echten Daten).</summary>
internal static class Fx
{
    public static PdfWord W(double x, double y, string text) => new(x, y, x + text.Length * 5, y + 10, text);

    public static IEnumerable<PdfWord> Line(double x, double y, string text)
    {
        double cx = x;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return W(cx, y, token);
            cx += token.Length * 5 + 4;
        }
    }

    public sealed record Row(string Date, string Type, string Amount, params string[] TextLines);

    public static PdfPageWords StatementPage(int pageIndex, IEnumerable<Row> rows,
        string? opening = null, string? closing = null, double startY = 352)
    {
        var words = new List<PdfWord>();
        words.AddRange(Line(70, 288, "Kontoauszug 9/2026 Seite 1 von 1"));
        words.AddRange(Line(70, 323, "Datum"));
        words.Add(W(123, 323, "Erläuterung"));
        words.Add(W(517, 323, "Betrag"));
        if (opening is not null)
        {
            words.AddRange(Line(123, 338, "Kontostand am 31.08.2026, Auszug Nr. 8"));
            words.Add(W(569 - opening.Length * 5, 338, opening));
        }

        double y = startY;
        foreach (var r in rows)
        {
            words.Add(W(70, y, r.Date));
            words.AddRange(Line(123, y, r.Type));
            words.Add(W(569 - r.Amount.Length * 5, y, r.Amount));
            double ty = y + 12;
            foreach (var t in r.TextLines)
            {
                words.AddRange(Line(123, ty, t));
                ty += 10;
            }
            y += 34;
        }

        if (closing is not null)
        {
            words.AddRange(Line(123, y + 10, "Kontostand am 30.09.2026 um 20:05 Uhr"));
            words.Add(W(569 - closing.Length * 5, y + 10, closing));
        }

        // Seitenfuß (muss ignoriert werden)
        words.AddRange(Line(70, 790, "Sparkasse Musterstadt Vorsitzender des Vorstandes 12.12.2026 99,99"));
        return new PdfPageWords(pageIndex, 595, 842, words);
    }

    public static Booking Classify(string type, decimal amount, string text, int year = 2026, AppRules? rules = null)
    {
        rules ??= new AppRules();
        var b = new Booking { Type = type, Amount = amount, Text = text, Date = new DateOnly(year, 9, 30) };
        new BookingClassifier(rules, year).Classify(b);
        rules.ApplyBeleglos(b);
        return b;
    }

    public static Booking Payment(decimal amount, string text, string type = "GutschriftÜberweisung", int year = 2026) =>
        Classify(type, amount, text, year);

    public static InvoiceRecord Inv(string number, decimal? amount, string recipient, DateOnly? date = null) =>
        new(number, number + ".pdf", amount, recipient, date);

    public static InvoiceIndex Index(params InvoiceRecord[] invoices)
    {
        var idx = new InvoiceIndex();
        foreach (var i in invoices) idx.Add(i);
        return idx;
    }
}
