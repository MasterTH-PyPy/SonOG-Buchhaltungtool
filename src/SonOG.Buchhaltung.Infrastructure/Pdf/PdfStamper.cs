using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Infrastructure.Pdf;

/// <summary>
/// Schreibt die laufende Nummer in PDFs: links neben jede Buchung im Kontoauszug und oben rechts auf die Belege.
/// Es wird immer eine Kopie bearbeitet. Der Sparkassen-Auszug ist qualifiziert signiert; die Signatur
/// ist in der gestempelten Kopie nicht mehr gültig, das Original muss deshalb separat archiviert werden.
/// </summary>
public static class PdfStamper
{
    static PdfStamper()
    {
        // PDFsharp 6: Unter Windows die installierten Schriften verwenden (Arial).
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    private static readonly XColor Blue = XColor.FromArgb(13, 51, 179);
    private static readonly XColor Green = XColor.FromArgb(0, 115, 51);

    public static void StampStatement(string sourcePdf, string targetPdf, IEnumerable<Booking> bookings)
    {
        using var doc = PdfReader.Open(sourcePdf, PdfDocumentOpenMode.Modify);
        var font = new XFont("Arial", 8, XFontStyleEx.Bold);

        foreach (var group in bookings.GroupBy(b => b.PageIndex))
        {
            var page = doc.Pages[group.Key];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var b in group)
            {
                var text = b.Beleglos ? b.Number + " BL" : b.Number;
                var brush = new XSolidBrush(b.Beleglos ? Green : Blue);
                double width = gfx.MeasureString(text, font).Width;
                // rechtsbündig an der linken Tabellenkante (x = 66 pt), auf der Grundlinie der ersten Buchungszeile
                gfx.DrawString(text, font, brush, 66 - width, b.Y1 - 1.5, XStringFormats.BaseLineLeft);
            }
        }

        doc.Save(targetPdf);
    }

    /// <summary>Stempelt die laufende Nummer oben rechts auf Seite 1 eines Belegs.</summary>
    public static void StampReceipt(string sourcePdf, string targetPdf, string number)
    {
        using var doc = PdfReader.Open(sourcePdf, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[0];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

        var font = new XFont("Arial", 11, XFontStyleEx.Bold);
        var text = "Beleg " + number;
        var size = gfx.MeasureString(text, font);
        double pad = 4;
        double x = page.Width.Point - 20 - size.Width - 2 * pad;
        double y = 14;

        var rect = new XRect(x, y, size.Width + 2 * pad, size.Height + 2 * pad - 2);
        gfx.DrawRectangle(new XPen(Blue, 0.9), XBrushes.White, rect);
        gfx.DrawString(text, font, new XSolidBrush(Blue), x + pad, y + pad + size.Height * 0.78, XStringFormats.BaseLineLeft);

        doc.Save(targetPdf);
    }

    /// <summary>Fügt PDFs in der übergebenen Reihenfolge zu einer Datei zusammen (Druckpaket).</summary>
    public static void Merge(IEnumerable<string> pdfFiles, string targetPdf)
    {
        using var output = new PdfDocument();
        foreach (var file in pdfFiles)
        {
            using var input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
            foreach (PdfPage page in input.Pages)
                output.AddPage(page);
        }
        output.Save(targetPdf);
    }

    /// <summary>Wie Merge, aber mit Seitenauswahl: Page = null nimmt alle Seiten, sonst nur die Seite (0-basiert).</summary>
    public static void MergePages(IEnumerable<(string File, int? Page)> items, string targetPdf)
    {
        using var output = new PdfDocument();
        var cache = new Dictionary<string, PdfDocument>();
        try
        {
            foreach (var (file, pageNo) in items)
            {
                if (!cache.TryGetValue(file, out var input))
                    cache[file] = input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                if (pageNo is { } n) output.AddPage(input.Pages[n]);
                else foreach (PdfPage page in input.Pages) output.AddPage(page);
            }
            output.Save(targetPdf);
        }
        finally
        {
            foreach (var d in cache.Values) d.Dispose();
        }
    }
}
