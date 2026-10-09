using System.Text;
using SonOG.Buchhaltung.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SonOG.Buchhaltung.Infrastructure.Pdf;

/// <summary>
/// Liest ein PDF mit PdfPig und liefert die Wörter im Koordinatensystem des Core-Projekts
/// (Punkt, Ursprung links oben). Die Wörter werden aus den einzelnen Buchstaben gebaut, weil der Kontoauszug
/// Datum und Erläuterung ohne Leerzeichen direkt nebeneinander setzt; an den angegebenen X-Positionen
/// wird deshalb immer ein Wort getrennt.
/// </summary>
public static class PdfWordReader
{
    /// <param name="maxPages">Nur die ersten n Seiten lesen (Rechnungen: Betrag steht meist am Ende, deshalb alle Seiten).</param>
    /// <param name="columnBreaks">X-Positionen (Punkt), an denen Wörter immer getrennt werden.</param>
    public static IReadOnlyList<PdfPageWords> Read(string path, int maxPages = int.MaxValue, IReadOnlyList<double>? columnBreaks = null)
    {
        columnBreaks ??= Array.Empty<double>();
        var result = new List<PdfPageWords>();

        using var doc = PdfDocument.Open(path);
        foreach (Page page in doc.GetPages())
        {
            if (page.Number > maxPages) break;
            result.Add(new PdfPageWords(page.Number - 1, page.Width, page.Height, ReadPage(page, columnBreaks)));
        }
        return result;
    }

    /// <summary>Gesamter Text des PDFs (zum Suchen von Bestellnummern in Amazon-Belegen).</summary>
    public static string ReadAllText(string path)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(path);
        foreach (Page page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private static List<PdfWord> ReadPage(Page page, IReadOnlyList<double> columnBreaks)
    {
        double height = page.Height;
        var words = new List<PdfWord>();

        // Zeilen bilden: Buchstaben nach Grundlinie (von oben nach unten) clustern.
        var letters = page.Letters.Where(l => !string.IsNullOrEmpty(l.Value)).OrderByDescending(l => l.StartBaseLine.Y).ToList();
        var line = new List<Letter>();
        double lineY = double.NaN;

        void FlushLine()
        {
            if (line.Count > 0) words.AddRange(BuildWords(line.OrderBy(l => l.StartBaseLine.X).ToList(), height, columnBreaks));
            line.Clear();
        }

        foreach (var l in letters)
        {
            if (double.IsNaN(lineY) || Math.Abs(l.StartBaseLine.Y - lineY) > 1.0)
            {
                FlushLine();
                lineY = l.StartBaseLine.Y;
            }
            line.Add(l);
        }
        FlushLine();
        return words;
    }

    private static IEnumerable<PdfWord> BuildWords(List<Letter> ordered, double pageHeight, IReadOnlyList<double> columnBreaks)
    {
        var sb = new StringBuilder();
        Letter? first = null;
        Letter? prev = null;

        PdfWord? Flush()
        {
            if (sb.Length == 0 || first is null || prev is null) { sb.Clear(); return null; }
            double size = first.PointSize > 0 ? first.PointSize : 10;
            double baseline = pageHeight - first.StartBaseLine.Y;
            // Zeilenoberkante/-unterkante aus der Grundlinie: alle Wörter einer Zeile bekommen so dasselbe Y0.
            var word = new PdfWord(first.StartBaseLine.X, baseline - size * 0.8, prev.EndBaseLine.X, baseline + size * 0.2, sb.ToString());
            sb.Clear();
            first = null;
            return word;
        }

        foreach (var l in ordered)
        {
            if (string.IsNullOrWhiteSpace(l.Value))
            {
                var w = Flush();
                if (w is not null) yield return w;
                prev = null;
                continue;
            }

            if (prev is not null)
            {
                double size = l.PointSize > 0 ? l.PointSize : 10;
                bool gap = l.StartBaseLine.X - prev.EndBaseLine.X > 0.25 * size;
                bool crossesBreak = columnBreaks.Any(b => prev.StartBaseLine.X < b && l.StartBaseLine.X >= b);
                if (gap || crossesBreak)
                {
                    var w = Flush();
                    if (w is not null) yield return w;
                }
            }

            first ??= l;
            sb.Append(l.Value);
            prev = l;
        }

        var last = Flush();
        if (last is not null) yield return last;
    }
}
