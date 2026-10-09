using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Parsing;

public sealed record TextLine(double Y, double X0, string Text, IReadOnlyList<PdfWord> Words);

public static class TextLines
{
    /// <summary>Gruppiert Wörter zu Zeilen (nach Y0) und sortiert innerhalb der Zeile von links nach rechts.</summary>
    public static List<TextLine> Group(IEnumerable<PdfWord> words, double tolerance = 2.5)
    {
        var result = new List<TextLine>();
        List<PdfWord>? current = null;
        double currentY = 0;

        void Flush()
        {
            if (current is { Count: > 0 })
            {
                var ordered = current.OrderBy(w => w.X0).ToList();
                result.Add(new TextLine(currentY, ordered[0].X0, string.Join(" ", ordered.Select(w => w.Text)), ordered));
            }
        }

        foreach (var w in words.OrderBy(w => w.Y0).ThenBy(w => w.X0))
        {
            if (current is null || w.Y0 - currentY > tolerance)
            {
                Flush();
                current = new List<PdfWord>();
                currentY = w.Y0;
            }
            current.Add(w);
        }
        Flush();
        return result;
    }
}
