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

    /// <summary>
    /// Rechnungsnummer aus zugeordneter Amazon-Rechnung oder aus dem Verwendungszweck; "" wenn keine erkennbar.
    /// Mit <paramref name="person"/>: steht kein "RE"/"Rechnung" davor, wird eine Zahl in der gelernten Form des Partners gesucht.
    /// </summary>
    public static string Aus(Booking b, PersonAccount? person = null)
    {
        var mitKennwort = AusKennwort(b);
        if (mitKennwort.Length > 0) return mitKennwort;
        return person?.RechnungsnummerFormen is { Count: > 0 } formen ? NachForm(b.Text, formen, b.Date.Year) : "";
    }

    private static string AusKennwort(Booking b)
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
    /// Form einer Nummer: Folge aus D(n) Ziffern, A(n) Buchstaben, sonstige Zeichen wörtlich; "J" vorne statt "D", wenn die
    /// Nummer mit dem Buchungsjahr beginnt (202606585 → "J9").
    /// </summary>
    public static string Form(string nummer, int jahr)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        bool jahrVorne = jahr > 0 && nummer.StartsWith(jahr.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        while (i < nummer.Length)
        {
            char c = nummer[i];
            int j = i;
            if (char.IsDigit(c)) { while (j < nummer.Length && char.IsDigit(nummer[j])) j++; sb.Append(i == 0 && jahrVorne ? 'J' : 'D').Append(j - i); }
            else if (char.IsLetter(c)) { while (j < nummer.Length && char.IsLetter(nummer[j])) j++; sb.Append('A').Append(j - i); }
            else { j++; sb.Append('~').Append(c); }
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>Die häufigsten Formen (höchstens drei, mindestens zweimal oder einzige), neuere Jahre zuerst gewichtet.</summary>
    public static List<string> Formen(IEnumerable<(string Nummer, int Jahr)> beispiele)
    {
        var liste = beispiele.ToList();
        int bis = liste.Count == 0 ? 0 : liste.Max(x => x.Jahr);
        var gewichtet = liste.GroupBy(x => Form(x.Nummer, x.Jahr))
            .Select(g => (Form: g.Key, Gewicht: g.Sum(x => x.Jahr == bis ? 3 : x.Jahr == bis - 1 ? 2 : 1), Anzahl: g.Count()))
            .Where(g => g.Form.Length > 0 && (g.Anzahl >= 2 || liste.Count == 1))
            .OrderByDescending(g => g.Gewicht).Take(3).Select(g => g.Form).ToList();
        return gewichtet;
    }

    /// <summary>Erstes Wort im Text, das einer der Formen entspricht (bei "J…" muss es mit dem Buchungsjahr oder Vorjahr beginnen).</summary>
    public static string NachForm(string text, IReadOnlyList<string> formen, int jahr)
    {
        foreach (var form in formen)
            foreach (Match w in Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9\-/\.]*[A-Za-z0-9]|\d+"))
            {
                var t = w.Value;
                foreach (var j in new[] { jahr, jahr - 1 })
                    if (Form(t, j) == form && t.Any(char.IsDigit)) return t;
            }
        return "";
    }

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
