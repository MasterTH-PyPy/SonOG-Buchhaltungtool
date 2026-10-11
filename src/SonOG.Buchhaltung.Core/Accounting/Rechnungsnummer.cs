using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>
/// Rechnungsnummer des Geschäftspartners zu einer Kontoauszugsbuchung, und Einsetzen in Buchungstexte aus Vorlagen,
/// die auf "Re-Nr.:" (oder Varianten wie "Re-Nr.", "Re.Nr.:", "Beleg-Nr.:") enden.
/// </summary>
public static class Rechnungsnummer
{
    /// <summary>Text endet auf einen Platzhalter für die Nummer.</summary>
    private static readonly Regex Platzhalter = new(@"(?:\bRe|\bBeleg|\bRechnungs?|\bRg)[\s\.\-]*Nr[\s\.:]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Amazon-Rechnungsnummer als Dateiname, z. B. DE66OD6DABEI.pdf.</summary>
    private static readonly Regex AmazonRechnung = new(@"^[A-Z]{2}[A-Z0-9]{8,14}$", RegexOptions.Compiled);

    /// <summary>Muster im Verwendungszweck; die Nummer muss mindestens eine Ziffer enthalten.</summary>
    private static readonly Regex[] Muster =
    {
        new(@"\bRG[\s\.\-:]*(\d{4,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),                                    // RG1026190417
        new(@"\b(?:RE|RG|RECHNUNG(?:SNR|SNUMMER)?)(?:[\s\.\-]*(?:NR|NUMMER))?[\s\.:\-#]+([A-Z0-9][A-Z0-9\-/]{3,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),                                                                 // RE-NR.: 69307919
        new(@"\bINV(?:OICE)?(?:[\s\.\-]*(?:NO|NR|NUMBER))?[\s\.:\-#]+([A-Z0-9][A-Z0-9\-/]{3,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>Rechnungsnummer aus zugeordneter Amazon-Rechnung oder aus dem Verwendungszweck; "" wenn keine erkennbar.</summary>
    public static string Aus(Booking b)
    {
        foreach (var f in b.ReceiptFiles)
        {
            var name = f.Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(name[(name.LastIndexOf('/') + 1)..]);
            if (AmazonRechnung.IsMatch(stem) && stem.Any(char.IsDigit)) return stem;
        }
        foreach (var rx in Muster)
            foreach (Match m in rx.Matches(b.Text))
            {
                var nr = m.Groups[1].Value.Trim('-', '/', '.');
                if (nr.Any(char.IsDigit) && !b.OwnInvoiceNumbers.Contains(nr)) return nr;
            }
        return "";
    }

    public static bool HatPlatzhalter(string text) => Platzhalter.IsMatch(text.TrimEnd());

    /// <summary>
    /// Hängt die Nummer an, wenn der Text auf "Re-Nr.:" o. Ä. endet. Bei Überlänge wird vorne gekürzt, nie die Nummer.
    /// </summary>
    public static string Einsetzen(string text, string nummer, int max = 60)
    {
        if (nummer.Length == 0 || !HatPlatzhalter(text)) return text.Length <= max ? text : text[..max].TrimEnd();
        var vorne = text.TrimEnd();
        int platz = max - nummer.Length - 1;
        if (platz < 1) return nummer.Length <= max ? nummer : nummer[..max];
        if (vorne.Length > platz) vorne = vorne[..platz].TrimEnd();
        return vorne + " " + nummer;
    }
}
